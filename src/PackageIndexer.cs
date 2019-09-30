using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Xml;
using System.Net.Http;
using Microsoft.Azure.Cosmos;
using System.Linq;
using Gremlin.Net.CosmosDb;
using Gremlin.Net.Driver;
using Gremlin.Net.Driver.Remote;
using Gremlin.Net.Process.Traversal;
using Gremlin.Net.Structure;
using static Gremlin.Net.Process.Traversal.AnonymousTraversalSource;
using static Gremlin.Net.Process.Traversal.__;
using static Gremlin.Net.Process.Traversal.P;
using static Gremlin.Net.Process.Traversal.Order;
using static Gremlin.Net.Process.Traversal.Operator;
using static Gremlin.Net.Process.Traversal.Pop;
using static Gremlin.Net.Process.Traversal.Scope;
using static Gremlin.Net.Process.Traversal.TextP;
using static Gremlin.Net.Process.Traversal.Column;
using static Gremlin.Net.Process.Traversal.Direction;
using static Gremlin.Net.Process.Traversal.T;
using Gremlin.Net.Structure.IO.GraphSON;
using System.Text.RegularExpressions;

namespace DependencyGraph
{
    public static class PackageIndexer
    {
        private static Container container;
        private static ILogger _log;

        [FunctionName("PackageIndexer")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            _log = log;
            _log.LogInformation("C# HTTP trigger function processed a request.");

            string name = req.Query["name"];
            string version = req.Query["version"];
            string frameworkFilter = req.Query["framework"];
            string license = req.Query["license"];

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            name = name ?? data?.name;
            version = version ?? data?.version;
            license = license ?? data?.license;
            frameworkFilter = frameworkFilter ?? data?.frameworkFilter;

            if (name == null || version == null || frameworkFilter == null || license == null)
            {
                return new BadRequestObjectResult("Needs more parameters");
            }

            var package = GetPackage(name, version, frameworkFilter);

            AssignLicense(package, license);

            return new OkObjectResult("Complete");
        }

        private static void AssignLicense(Package package, string license)
        {
            _log.LogInformation($"Assign License: {package.Name}:{package.Version} - {license}");
            using (var gremlinClient = GraphConnection())
            {
                var g = Traversal().WithRemote(new DriverRemoteConnection(gremlinClient));
                var l = gremlinClient.SubmitWithSingleResultAsync<dynamic>($"g.{g.V().Has("license", "id", license).ToGremlinQuery()}").Result;

                if (l == null)
                {
                    l = gremlinClient.SubmitAsync<dynamic>($"g.{g.AddV("license").Property("id", license).Property("Name", license).ToGremlinQuery()}").Result;
                }

                var lun = gremlinClient.SubmitWithSingleResultAsync<dynamic>($"g.{g.V().Has("license","Name", package.LicenseUrl).ToGremlinQuery()}").Result;

                var command = $"V('{package.Id}').AddE('licensed').To(__.V('{license}'))";
                var v = gremlinClient.SubmitAsync<dynamic>($"g.{command}").Result;

                command = $"V('{lun["id"]}').AddE('ofType').To(__.V('{license}'))";
                v = gremlinClient.SubmitAsync<dynamic>($"g.{command}").Result;
            }
        }

        private static Package GetPackage(string name, string version, string frameworkFilter)
        {
            _log.LogInformation($"Get Package: {name}:{version}");

            var url = $"https://www.nuget.org/api/v2/Packages(Id='{name}',Version='{version}')";

            var xml = new XmlDocument();

            using (var httpClient = new HttpClient())
            {
                var result = httpClient.GetStreamAsync(url);

                xml.Load(result.Result);

                var dns = xml.GetNamespaceOfPrefix("d");
                var mns = xml.GetNamespaceOfPrefix("m");

                var properties = xml.GetElementsByTagName("m:properties");

                var package = new Package
                {

                    Name = xml.GetElementsByTagName("d:Id")[0].InnerText,

                    Version = xml.GetElementsByTagName("d:Version")[0].InnerText,
                    LicenseUrl = xml.GetElementsByTagName("d:LicenseUrl")[0].InnerText,
                    DownloadUrl = (xml.GetElementsByTagName("content")[0]).Attributes["src"].InnerText
                };

                package.Id = $"{package.Name}:{package.Version}";

                StorePackage(package);

                var depends = xml.GetElementsByTagName("d:Dependencies")[0].InnerText;

                if (depends.Length > 0)
                {
                    foreach (var depend in depends.Split('|'))
                    {
                        var parts = depend.Split(':');

                        var frameworkPart = parts[2];

                        if (frameworkPart.Equals(frameworkFilter, StringComparison.InvariantCultureIgnoreCase))
                        {
                            string namePart = parts[0];
                            string versionPart = parts[1];


                            if (!string.IsNullOrEmpty(namePart))
                            {
                                versionPart = Regex.Match(versionPart, "[^[,]+").Groups[0].Value;
                                _log.LogInformation($"Getting Dependent {namePart}:{versionPart}");

                                GetPackage(namePart, versionPart, frameworkFilter);
                                DependsOn($"{package.Id}", $"{namePart}:{versionPart}", frameworkPart);
                            }
                        }
                    }
                }

                return package;
            }
        }

        private static void DependsOn(string parent, string dependent, string framework)
        {
            _log.LogInformation($"Add DependsOn: {parent}:{dependent}");

            using (var gremlinClient = GraphConnection())
            {
                var g = Traversal().WithRemote(new DriverRemoteConnection(gremlinClient));
                var command = $"V('{parent}').AddE('dependsOn').Property('Framework','{framework}').To(__.V('{dependent}'))";
                var result = gremlinClient.SubmitAsync<dynamic>($"g.{command}").Result;
                _log.LogInformation("DependsOn done");
            }
        }

        private static void StorePackage(Package package)
        {
            _log.LogInformation($"Store Package: {package.Id}");

            using (var gremlinClient = GraphConnection())
            {
                var g = Traversal().WithRemote(new DriverRemoteConnection(gremlinClient));

                var check = g.V().Has("package", "id", $"{package.Id}");

                var v = gremlinClient.SubmitWithSingleResultAsync<dynamic>($"g.{check.ToGremlinQuery()}").Result;

                if (v == null)
                {
                    _log.LogInformation("Create Package");
                    var command = g.AddV("package")
                        .Property("id", $"{package.Id}")
                        .Property("Name", package.Name)
                        .Property("Version", package.Version)
                        .Property("LicenseUrl", package.LicenseUrl)
                        .Property("DownloadUrl", package.DownloadUrl);

                    v = gremlinClient.SubmitWithSingleResultAsync<dynamic>($"g.{command.ToGremlinQuery()}").Result;

                }

                StoreLicense(package);

                _log.LogInformation("Store Done");

            }
        }

        private static void StoreLicense(Package package)
        {
            _log.LogInformation($"Store license: {package.Id} : {package.LicenseUrl}");

            using (var gremlinClient = GraphConnection())
            {
                var g = Traversal().WithRemote(new DriverRemoteConnection(gremlinClient));
                var check = g.V().Has("license", "Name", $"{package.LicenseUrl}");
                var w = gremlinClient.SubmitWithSingleResultAsync<dynamic>($"g.{check.ToGremlinQuery()}").Result;

                if (w == null)
                {
                    var command = g.AddV("license").Property("Name", package.LicenseUrl);

                    w = gremlinClient.SubmitWithSingleResultAsync<dynamic>($"g.{command.ToGremlinQuery()}").Result;

                }

                _log.LogInformation($"Adding Edge from {package.Id} to {w["id"]}");

                gremlinClient.SubmitAsync($"g.V('{package.Id}').AddE('license').To(__.V('{w["id"]}'))").Wait();

            }
        }

        private static GremlinClient GraphConnection()
        {
            var connectionString = Environment.GetEnvironmentVariable("connectionString");
            var password = Environment.GetEnvironmentVariable("key");
            var databaseId = Environment.GetEnvironmentVariable("databaseId");
            var containerId = Environment.GetEnvironmentVariable("containerId");

            var gremlinServer = new GremlinServer(connectionString, 443, true, $"/dbs/{databaseId}/colls/{containerId}", password);
            var gremlinClient = new GremlinClient(gremlinServer, new GraphSON2Reader(), new GraphSON2Writer(), GremlinClient.GraphSON2MimeType);

            return gremlinClient;
        }
    }
}