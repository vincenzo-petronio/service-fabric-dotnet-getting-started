// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace WebService.Controllers
{
    using Microsoft.AspNetCore.Mvc;
    using Newtonsoft.Json;
    using System;
    using System.Collections.Generic;
    using System.Fabric;
    using System.Fabric.Query;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Threading.Tasks;

    [Route("api/[controller]")]
    public class StatefulBackendServiceController : Controller
    {
        private readonly HttpClient httpClient;
        private readonly StatelessServiceContext serviceContext;
        private readonly ConfigSettings configSettings;
        private readonly FabricClient fabricClient;

        public StatefulBackendServiceController(StatelessServiceContext serviceContext, HttpClient httpClient, FabricClient fabricClient, ConfigSettings settings)
        {
            this.serviceContext = serviceContext;
            this.httpClient = httpClient;
            this.configSettings = settings;
            this.fabricClient = fabricClient;
        }

        // GET: api/values
        [HttpGet]
        public async Task<IActionResult> GetAsync()
        {
            // the stateful service service may have more than one partition.
            // this sample code uses a very basic loop to aggregate the results from each partition to illustrate aggregation.
            // note that this can be optimized in multiple ways for production code.
            string serviceUri = this.serviceContext.CodePackageActivationContext.ApplicationName + "/" + this.configSettings.StatefulBackendServiceName;
            ServicePartitionList partitions = await this.fabricClient.QueryManager.GetPartitionListAsync(new Uri(serviceUri));

            List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();

            JsonSerializer serializer = new JsonSerializer();
            foreach (Partition partition in partitions)
            {
                long partitionKey = ((Int64RangePartitionInformation)partition.PartitionInformation).LowKey;

                // REVERSE PROXY 
                // https://docs.microsoft.com/it-it/azure/service-fabric/service-fabric-reverseproxy
                // Per raggiungere un URL internamente (non dall'esterno) su una partizione di tipo Ranged,
                // è sufficiente chiamare l'API in questo modo:
                string proxyUrl =
                    $"http://localhost:{this.configSettings.ReverseProxyPort}/{serviceUri.Replace("fabric:/", "")}/api/values?PartitionKind={partition.PartitionInformation.Kind}&PartitionKey={partitionKey}";

                HttpResponseMessage response = await this.httpClient.GetAsync(proxyUrl);

                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    // if one partition returns a failure, you can either fail the entire request or skip that partition.
                    return this.StatusCode((int)response.StatusCode);
                }

                List<KeyValuePair<string, string>> list =
                    JsonConvert.DeserializeObject<List<KeyValuePair<string, string>>>(await response.Content.ReadAsStringAsync());

                List<KeyValuePair<string, string>> listEnhanced = list.Select(kv =>
                    new KeyValuePair<string, string>(
                        kv.Key, kv.Value + " - " + partition.PartitionInformation.Id + " - " + partition.PartitionInformation.Kind
                    )).ToList();

                if (listEnhanced != null && listEnhanced.Any())
                {
                    result.AddRange(listEnhanced);
                }
            }

            return this.Json(result);
        }

        // PUT api/values
        [HttpPut]
        public async Task<IActionResult> PutAsync([FromBody] KeyValuePair<string, string> keyValuePair)
        {
            string serviceUri = this.serviceContext.CodePackageActivationContext.ApplicationName.Replace("fabric:/", "") + "/" +
                                this.configSettings.StatefulBackendServiceName;
            int partitionKeyNumber;

            try
            {
                string key = keyValuePair.Key;

                // Should we validate this in the UI or here in the controller?
                if (!String.IsNullOrEmpty(key))
                {
                    partitionKeyNumber = GetPartitionKey(key);
                }
                else
                {
                    throw new ArgumentException("No key provided");
                }
            }
            catch (Exception ex)
            {
                return new ContentResult { StatusCode = 400, Content = ex.Message };
            }

            // REVERSE PROXY 
            string proxyUrl =
                $"http://localhost:{this.configSettings.ReverseProxyPort}/{serviceUri}/api/values/{keyValuePair.Key}?PartitionKind=Int64Range&PartitionKey={partitionKeyNumber}";

            string payload = $"{{ 'value' : '{keyValuePair.Value}' }}";
            StringContent putContent = new StringContent(payload, Encoding.UTF8, "application/json");
            putContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            HttpResponseMessage response = await this.httpClient.PutAsync(proxyUrl, putContent);

            return new ContentResult()
            {
                StatusCode = (int)response.StatusCode,
                Content = await response.Content.ReadAsStringAsync()
            };
        }


        // GET api/values/5
        [HttpGet("{id}")]
        public string Get(int id)
        {
            throw new NotImplementedException("No method implemented to get a specific key/value pair from the Stateful Backend Service");
        }


        // DELETE api/values/5
        [HttpDelete("{id}")]
        public void Delete(int id)
        {
            throw new NotImplementedException("No method implemented to delete a specified key/value pair in the Stateful Backend Service");
        }

        private static int GetPartitionKey(string key)
        {
            // The partitioning scheme of the processing service is a range of integers from 0 - 25.
            // This generates a partition key within that range by converting the first letter of the input name
            // into its numerica position in the alphabet.
            char firstLetterOfKey = key.First();
            int partitionKeyInt = Char.ToUpper(firstLetterOfKey) - 'A';

            if (partitionKeyInt < 0 || partitionKeyInt > 25)
            {
                throw new ArgumentException("The key must begin with a letter between A and Z");
            }

            return partitionKeyInt;
        }
    }
}