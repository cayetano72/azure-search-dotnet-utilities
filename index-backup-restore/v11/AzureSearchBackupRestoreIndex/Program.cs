﻿// This is a prototype tool that allows for extraction of data from a search index
// Since this tool is still under development, it should not be used for production usage

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Models;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace AzureSearchBackupRestoreIndex;

class Program
{
    private static string SourceSearchServiceName;
    private static string SourceIndexName;
    private static string TargetSearchServiceName;
    private static string TargetIndexName;
    private static string BackupDirectory;

    private static SearchIndexClient SourceIndexClient;
    private static SearchClient SourceSearchClient;
    private static SearchIndexClient TargetIndexClient;
    private static SearchClient TargetSearchClient;
    private static int MaxBatchSize = 500;          // JSON files will contain this many documents / file and can be up to 1000
    private static int ParallelizedJobs = 5;       // Output content in parallel jobs
    private static AzureDeveloperCliCredential credential; // credential for Azure Search
    private static AccessToken token; // Request token for Azure Cognitive Search



    static void Main()
    {
        //Get source and target search service info and index names from appsettings.json file
        //Set up source and target search service clients
        ConfigurationSetup();

        //Backup the source index
        Console.WriteLine("\nSTART INDEX BACKUP");
        BackupIndexAndDocuments();

        //Recreate and import content to target index

        Console.WriteLine("\nSTART INDEX RESTORE");
        DeleteIndex();
        CreateTargetIndex();
        ImportFromJSON();

        Console.WriteLine("\n  Waiting 10 seconds for target to index content before validating...");
        Thread.Sleep(10000);

        // Validate all content is in target index
        int sourceCount = GetCurrentDocCount(SourceSearchClient);
        int targetCount = GetCurrentDocCount(TargetSearchClient);
        Console.WriteLine("\nSAFEGUARD CHECK: Source and target index counts should match");
        Console.WriteLine(" Source index contains {0} docs", sourceCount);
        Console.WriteLine(" Target index contains {0} docs\n", targetCount);
    }

    static void ConfigurationSetup()
    {

        IConfigurationBuilder builder = new ConfigurationBuilder().AddJsonFile("appsettings.json");
        IConfigurationRoot configuration = builder.Build();

        SourceSearchServiceName = configuration["SourceSearchServiceName"];
        SourceIndexName = configuration["SourceIndexName"];
        TargetSearchServiceName = configuration["TargetSearchServiceName"];
        TargetIndexName = configuration["TargetIndexName"];
        BackupDirectory = configuration["BackupDirectory"];

        Console.WriteLine("CONFIGURATION:");
        Console.WriteLine("\n  Source service and index {0}, {1}", SourceSearchServiceName, SourceIndexName);
        Console.WriteLine("\n  Target service and index: {0}, {1}", TargetSearchServiceName, TargetIndexName);
        Console.WriteLine("\n  Backup directory: " + BackupDirectory);

        // Create the credential and request a token for Azure Cognitive Search:
        credential = new AzureDeveloperCliCredential();

        SourceIndexClient = new SearchIndexClient(new Uri("https://" + SourceSearchServiceName + ".search.windows.net"), credential);
        SourceSearchClient = SourceIndexClient.GetSearchClient(SourceIndexName);

        TargetIndexClient = new SearchIndexClient(new Uri($"https://" + TargetSearchServiceName + ".search.windows.net"), credential);
        TargetSearchClient = TargetIndexClient.GetSearchClient(TargetIndexName);
        
        token = credential.GetToken(
            new TokenRequestContext(new[] { "https://search.azure.com/.default" } )
        );
    }

    static void BackupIndexAndDocuments()
    {
        // Backup the index schema to the specified backup directory
        Console.WriteLine("\n Backing up source index schema to {0}\n", Path.Combine(BackupDirectory, SourceIndexName + ".schema"));

        File.WriteAllText(Path.Combine(BackupDirectory, SourceIndexName + ".schema"), GetIndexSchema());

        // Extract the content to JSON files
        int SourceDocCount = GetCurrentDocCount(SourceSearchClient);
        WriteIndexDocuments(SourceDocCount);     // Output content from index to json files
    }

    static void WriteIndexDocuments(int CurrentDocCount)
    {
        // Write document files in batches (per MaxBatchSize) in parallel
        int FileCounter = 0;
        for (int batch = 0; batch <= (CurrentDocCount / MaxBatchSize); batch += ParallelizedJobs)
        {

            List<Task> tasks = new List<Task>();
            for (int job = 0; job < ParallelizedJobs; job++)
            {
                FileCounter++;
                int fileCounter = FileCounter;
                if ((fileCounter - 1) * MaxBatchSize < CurrentDocCount)
                {
                    Console.WriteLine(" Backing up source documents to {0} - (batch size = {1})", Path.Combine(BackupDirectory, SourceIndexName + fileCounter + ".json"), MaxBatchSize);

                    tasks.Add(Task.Factory.StartNew(() =>
                        ExportToJSON((fileCounter - 1) * MaxBatchSize, Path.Combine(BackupDirectory, $"{SourceIndexName}{fileCounter}.json"))
                    ));
                }

            }
            Task.WaitAll(tasks.ToArray());  // Wait for all the stored procs in the group to complete
        }

        return;
    }

    static void ExportToJSON(int Skip, string FileName)
    {
        // Extract all the documents from the selected index to JSON files in batches of 500 docs / file
        string json = string.Empty;
        try
        {
            Console.WriteLine("Export to {0} - Skip: {1} STARTED", FileName, Skip);

            SearchOptions options = new SearchOptions()
            {
                SearchMode = SearchMode.All,
                Size = MaxBatchSize,
                Skip = Skip
            };

            SearchResults<SearchDocument> response = SourceSearchClient.Search<SearchDocument>("*", options);

            foreach (var doc in response.GetResults())
            {
                json += JsonSerializer.Serialize(doc.Document) + ",";
                json = json.Replace("\"Latitude\":", "\"type\": \"Point\", \"coordinates\": [");
                json = json.Replace("\"Longitude\":", "");
                json = json.Replace(",\"IsEmpty\":false,\"Z\":null,\"M\":null,\"CoordinateSystem\":{\"EpsgId\":4326,\"Id\":\"4326\",\"Name\":\"WGS84\"}", "]");
                json += "\n";
            }

            // Output the formatted content to a file
            json = json.Substring(0, json.Length - 3); // remove trailing comma
            File.WriteAllText(FileName, "{\"value\": [");
            File.AppendAllText(FileName, json);
            File.AppendAllText(FileName, "}]}");
            Console.WriteLine("  Total documents for file {0}, skip:{1}: {2}", FileName, Skip, response.GetResults().Count().ToString());
            json = string.Empty;

            Console.WriteLine("Export to {0} - Skip: {1} DONE", FileName, Skip);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: {0}", ex.Message);
        }
    }

    static string GetIDFieldName()
    {
        // Find the id field of this index
        string IDFieldName = string.Empty;
        try
        {
            var schema = SourceIndexClient.GetIndex(SourceIndexName);
            foreach (var field in schema.Value.Fields)
            {
                if (field.IsKey == true)
                {
                    IDFieldName = Convert.ToString(field.Name);
                    break;
                }
            }

        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: {0}", ex.Message);
        }

        return IDFieldName;
    }

    static string GetIndexSchema()
    {
        // Extract the schema for this index
        // We use REST here because we can take the response as-is

        Uri ServiceUri = new Uri($"https://{SourceSearchServiceName}.search.windows.net");

        HttpClient HttpClient = new HttpClient();
        HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        string Schema = string.Empty;

        try
        {
            Uri uri = new Uri(ServiceUri, $"/indexes/{SourceIndexName}");
            HttpResponseMessage response = AzureSearchHelper.SendSearchRequest(HttpClient, HttpMethod.Get, uri);
            AzureSearchHelper.EnsureSuccessfulSearchResponse(response);
            Schema = response.Content.ReadAsStringAsync().Result;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: {0}", ex.Message);
        }

        return Schema;
    }

    private static bool DeleteIndex()
    {
        Console.WriteLine("\n  Delete target index {0} in {1} search service, if it exists", TargetIndexName, TargetSearchServiceName);        
        // Delete the index if it exists
        try
        {
            TargetIndexClient.DeleteIndex(TargetIndexName);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Error deleting index: {0}\n", ex.Message);
            Console.WriteLine("  Did you remember to set your SearchServiceName and SearchServiceApiKey?\n");
            return false;
        }

        return true;
    }

    static void CreateTargetIndex()
    {
        Console.WriteLine("\n  Create target index {0} in {1} search service", TargetIndexName, TargetSearchServiceName);
        // Use the schema file to create a copy of this index
        // I like using REST here since I can just take the response as-is

        string json = File.ReadAllText(Path.Combine(BackupDirectory, SourceIndexName + ".schema"));

        // Do some cleaning of this file to change index name, etc
        json = "{" + json.Substring(json.IndexOf("\"name\""));
        int indexOfIndexName = json.IndexOf("\"", json.IndexOf("name\"") + 5) + 1;
        int indexOfEndOfIndexName = json.IndexOf("\"", indexOfIndexName);
        json = json.Substring(0, indexOfIndexName) + TargetIndexName + json.Substring(indexOfEndOfIndexName);

        Uri ServiceUri = new Uri("https://" + TargetSearchServiceName + ".search.windows.net");

        HttpClient HttpClient = new HttpClient();
        HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        try
        {
            Uri uri = new Uri(ServiceUri, "/indexes");
            HttpResponseMessage response = AzureSearchHelper.SendSearchRequest(HttpClient, HttpMethod.Post, uri, json);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Error: {0}", ex.Message);
        }
    }

    static int GetCurrentDocCount(SearchClient searchClient)
    {
        // Get the current doc count of the specified index
        try
        {
            SearchOptions options = new SearchOptions
            {
                SearchMode = SearchMode.All,
                IncludeTotalCount = true
            };

            SearchResults<Dictionary<string, object>> response = searchClient.Search<Dictionary<string, object>>("*", options);
            return Convert.ToInt32(response.TotalCount);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Error: {0}", ex.Message);
        }

        return -1;
    }

    static void ImportFromJSON()
    {
        Console.WriteLine("\n  Upload index documents from saved JSON files");
        // Take JSON file and import this as-is to target index

        Uri ServiceUri = new Uri("https://" + TargetSearchServiceName + ".search.windows.net");

        HttpClient HttpClient = new HttpClient();
        HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        try
        {
            foreach (string fileName in Directory.GetFiles(BackupDirectory, SourceIndexName + "*.json"))
            {
                Console.WriteLine("  -Uploading documents from file {0}", fileName);
                string json = File.ReadAllText(fileName);
                Uri uri = new Uri(ServiceUri, "/indexes/" + TargetIndexName + "/docs/index");
                HttpResponseMessage response = AzureSearchHelper.SendSearchRequest(HttpClient, HttpMethod.Post, uri, json);
                response.EnsureSuccessStatusCode();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Error: {0}", ex.Message);
        }
    }
}
