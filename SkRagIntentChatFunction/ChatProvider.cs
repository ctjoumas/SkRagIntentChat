﻿using SkRagIntentChatFunction.Models;

namespace SkRagIntentChatFunction
{
    using Microsoft.Extensions.Logging;
    using Microsoft.SemanticKernel.ChatCompletion;
    using Microsoft.SemanticKernel;
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Azure.AI.OpenAI;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.SemanticKernel.Connectors.OpenAI;
    using System.IO;
    using SkRagIntentChatFunction.Models;
    using Microsoft.AspNetCore.Mvc;
    using SkRagIntentChatFunction.Interfaces;
    using SkRagIntentChatFunction.Services;
    using System.Collections;
    using SemanticKernel.Data.Nl2Sql.Harness;
    using Google.Protobuf;
    using Microsoft.Azure.Cosmos.Serialization.HybridRow;

    public class ChatProvider
    {
        private readonly ILogger<ChatProvider> _logger;
        private readonly Kernel _kernel;
        private readonly IChatCompletionService _chat;
        private readonly ChatHistory _chatHistory;

        private string _deploymentName = Environment.GetEnvironmentVariable("ApiDeploymentName", EnvironmentVariableTarget.Process) ?? string.Empty;
        private string _azureOpenAiEndpoint = Environment.GetEnvironmentVariable("OpenAiEndpoint", EnvironmentVariableTarget.Process) ?? string.Empty;
        private string _azureOpenAiApiKey = Environment.GetEnvironmentVariable("OpenAiApiKey", EnvironmentVariableTarget.Process) ?? string.Empty;

        private AzureAIAssistantService _azureAIAssistantService;
        private AzureBlobService _azureBlobService;
        private IAzureCosmosDbService _azureCosmosDbService;

        public ChatProvider(
            ILogger<ChatProvider> logger, 
            Kernel kernel, IChatCompletionService chat, 
            ChatHistory chatHistory,
            IAzureCosmosDbService azureCosmosDbService)
        {
            _logger = logger;
            _kernel = kernel;
            _chat = chat;
            _chatHistory = chatHistory;
            _azureAIAssistantService = new AzureAIAssistantService(_azureOpenAiEndpoint, _azureOpenAiApiKey, _deploymentName);
            // _kernel.ImportPluginFromObject(new TextAnalyticsPlugin(_client));

            _azureBlobService = new AzureBlobService
            {
                ConnectionString = Environment.GetEnvironmentVariable("BlobConnectionString", EnvironmentVariableTarget.Process),
                ContainerName = Environment.GetEnvironmentVariable("ContainerName", EnvironmentVariableTarget.Process)
            };

            _azureCosmosDbService = azureCosmosDbService;
        }

        [Function("ChatProvider")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            // Request body example:
            /*
                {
                    "userId": "stevesmith@contoso.com",
                    "sessionId": "12345678",
                    "tenantId": "00001",
                    "chatName": "New Chat",
                    "prompt": "Hello, What can you do for me?"
                }
            */

            var sqlHarness = new SqlSchemaProviderHarness();

            _chatHistory.Clear();

            _logger.LogInformation("C# HTTP SentimentAnalysis trigger function processed a request.");

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var chatRequest = JsonSerializer.Deserialize<ChatProviderRequest>(requestBody);
            if (chatRequest == null || chatRequest.userId == null || chatRequest.sessionId == null || chatRequest.tenantId == null || chatRequest.prompt == null)
            {
                throw new ArgumentNullException("Please check your request body, you are missing required data.");
            }

            if (string.IsNullOrEmpty(chatRequest.sessionId))
            {
                // needed for new chats
                chatRequest.sessionId = Guid.NewGuid().ToString();
            }

            // insert session if it doesn't already exist
            bool sessionExists = await _azureCosmosDbService.SessionExists(chatRequest.sessionId);
            if (!sessionExists)
            {
                Session session = new Session
                {
                    Id = Guid.NewGuid().ToString(),
                    SessionId = chatRequest.sessionId,
                    Name = chatRequest.chatName,
                    Type = "session",
                    Timestamp = DateTime.UtcNow
                };

                await _azureCosmosDbService.InsertSessionAsync(session);
            }

            var response = new ChatProviderResponse();
            var intent = await Util.GetIntent(_chat, chatRequest.prompt);

            // if the intent has "-image" appended, we will need to use assistant API to generate the image
            // once the response is received, so determine if this is part of the intent, then strip it off
            // so we can process the root intent
            bool renderImageWithResponse = intent.EndsWith("-image");

            if (renderImageWithResponse) {
                intent = intent.Substring(0, intent.Length - "-image".Length);
            }

            // all database intents will the same thing, so in the switch statement we'll build the
            // schemas based on the type of database intent and then build and add the system and
            // user messages to the chat history outside of the switch so it isn't duplicated
            bool databaseIntent = false;

            //var dbSchema = string.Empty;
            var jsonSchema = string.Empty;
            string[] tableNames = null;

            // The purpose of using an Intent pattern is to allow you to make decisions about how you want to invoke the LLM.
            // In the case of RAG, if you can detect the user intent is related to searching manuals, then you can perform
            // only that action when the intent is to search manuals. This allows you to reduce the token usage and
            // save you TPM and cost
            switch (intent)
            {
                case "manual":
                    {
                        Console.WriteLine("Intent: manual");

                        var function = _kernel.Plugins.GetFunction("AzureAISearchPlugin", "SearchManualsIndex");
                        var responseContent = await _kernel.InvokeAsync(function, new() { ["query"] = chatRequest.prompt });
                        _chatHistory.AddUserMessage(responseContent.ToString());
                        _chatHistory.AddUserMessage(chatRequest.prompt);

                        break;
                    }
                case "databaseproduct":
                    {
                        databaseIntent = true;

                        // At this point we know the intent is database related so we could just call the plugin
                        // directly like the manuals above, but since we have AutoInvokeKernelFunctions enabled,
                        // we can just let SK detect that it needs to call the function and let it do it. However,
                        // it would be more performant to just call it directly as there is additional overhead
                        // with SK searching the plugin collection.
                        Console.WriteLine("Intent: databaseproduct");

                        //dbSchema = Util.GetDatabaseSchema();
                        
                        tableNames = "SalesLT.Product,SalesLT.ProductCategory, SalesLT.ProductDescription, SalesLT.ProductModel,SalesLT.ProductModelProductDescription".Split(",");
                        jsonSchema = await sqlHarness.ReverseEngineerSchemaJSONAsync(tableNames);

                        break;
                    }
                case "databasecustomer":
                    {
                        databaseIntent = true;

                        // At this point we know the intent is database related so we could just call the plugin
                        // directly like the manuals above, but since we have AutoInvokeKernelFunctions enabled,
                        // we can just let SK detect that it needs to call the function and let it do it. However,
                        // it would be more performant to just call it directly as there is additional overhead
                        // with SK searching the plugin collection.
                        Console.WriteLine("Intent: databasecustomer");

                        //dbSchema = Util.GetDatabaseSchema();
                        
                        tableNames = "SalesLT.Address,SalesLT.Customer, SalesLT.CustomerAddress".Split(",");
                        jsonSchema = await sqlHarness.ReverseEngineerSchemaJSONAsync(tableNames);

                        break;
                    }
                case "not_found":
                    {
                        Console.WriteLine("Intent: not_found");

                        // setting this to true assuming an image is being requested; if it is not and is a generic question, FileType will
                        // not be returned and a file will not attempt to be generated
                        renderImageWithResponse = true;

                        var systemPrompt = $@"
                                        You are responsible for checking the chat history to determine if an image or file is being requested to be generated as well
                                        as what type of chart the user asked to be generated, such as a bar chart, histogram, pie chart, etc, and any specifics about
                                        the chart such as number of slides, color of bars, etc. If a file was requested, you must respond with the kind of file/chart
                                        the user asked for in the chat history as well as the data that was retrieved in the chat history. You must also respond with
                                        the type of chart requested, which will be in a previous prompt in the chat history. If the current prompt is unrelated to
                                        file generation, you will not reply with any historical context and must respond in a friendly manner to the best of your
                                        ability based on the context of the user prompt, while also adding a keyword 'Unrelated'.
                                        Perform each of the following steps if the user prompt is related to file generation:
                                        1. Find in chat history the data returned from the result of a SQL query. You must respond with this data.
                                        2. Find in chat history the type of chart the user requested. You must respond with this type of chart.
                                        3. In the current user request, determine if any specifics are stated such as number of slides, color of bars, etc. You must repsond with this data.
                                        3. In the current user request, validate the file type (PNG, PPT, DOC, HTML, JPEG, JPG, GIF, PNG, XLS, or PDF).
                                          - If valid, add to the response: FileType: <file type>
                                          - If invalid, add to the response: InvalidFileType: <file type>. Please choose from the following file types: PNG, PPT, DOC, HTML, JPEG, JPG, GIF, PNG, XLS, or PDF.                                       
                                          - If the file type is not specified, do not append anything to the response.

                                        Examples:                                       
                                        1. Valid File Request:
                                            - User Prompt: 'Save this as a powerpoint.'
                                            - Response: '<Results from chat history including the data returned from the SQL query, the type of chart to be generated, such as bar chart, pie chart, histogram, etc., and any specifics about the chart, such as number of slides, color of bars, etc..>. RequestedFormat: FileType: PPT.'

                                        2. Valid File Request:
                                            - User Prompt: 'Save this as a PNG.'
                                            - Response: '<Results from chat history including the data returned from the SQL query, the type of chart to be generated, such as bar chart, pie chart, histogram, etc., and any specifics about the chart, such as number of slides, color of bars, etc..>. RequestedFormat: FileType: PPT.'

                                        3. Invalid File Request:
                                            - User Prompt: 'Save this as a PBX.'
                                            - Response: 'InvalidFileType: Please choose from the following file types: PNG, PPT, DOC, HTML, JPEG, JPG, GIF, PNG, XLS, or PDF.'

                                        4. Unrelated Request:
                                            - User Prompt: 'Hello'
                                            - Response: 'Unrelated: Hello!'

                                        5. Unrelated Request:
                                            - User Prompt: 'Thank you'
                                            - Response: 'Unrelated: You're welcome!'
                                    ";

                        // it's possible the user prompt is a follow up to save a file of a proper type, or
                        // any generic unrelated message such as "Hello", so we need to build the seystem message accordingly.
                        _chatHistory.AddSystemMessage(systemPrompt);

                        break;
                    }
            }

            if (databaseIntent)
            {
                var systemPrompt = $@"You are responsible for generating and executing a SQL query in response to user input.
                                    Only target the tables described in the given database schema.

                                    Perform each of the following steps:
                                    1. Generate a query that is always entirely based on the targeted database schema.
                                    2. Execute the query using the available plugin.
                                    3. Summarize the results to the user.
                                    4. Summarize whether or not a file is requested to be saved or generated. In addition to providing the response from the previous steps, you will also respond with the type of file requested:
                                        - Validate the file type (PNG, PPT, DOC, HTML, JPEG, JPG, GIF, PNG, XLS, or PDF).
                                        - If valid, add to the response: FileType: <file type>
                                        - If invalid, add to the response: InvalidFileType: <file type>. Please choose from the following file types: PNG, PPT, DOC, HTML, JPEG, JPG, GIF, PNG, XLS, or PDF.                                       
                                        - If the file type is not specified, do not append anything to the response.

                                        Examples:                                       
                                        1. Valid File Request:
                                            - User Prompt: 'In a bar chart, show me how many customers are aassigned to each salesperson. Save this as a powerpoint.'
                                            - Response: '<response from steps 1 through 3>. FileType: PPT.'
                                            This response must be added to the response you have from steps 1 through 3. If you do not include the response from steps 1 through 3, everything else will break. This response must start with 'FileType'. If you do not include 'FileType', everything else will break.

                                        2. Valid File Request:
                                            - User Prompt: 'Which month has the most sales? Plot each months sales for the most current year in a pie chart and save this as a PNG.'
                                            - Response: '<response from steps 1 through 3>. FileType: PNG.'
                                            This response must be added to the response you have from steps 1 through 3. If you do not include the response from steps 1 through 3, everything else will break. This response must start with 'FileType'. If you do not include 'FileType', everything else will break.

                                        3. Invalid File Request:
                                            - User Prompt: 'In a bar chart, show me how many customers are assigned to each salesperson. Save this as a PBX.'
                                            - Response: '<response from steps 1 through 3>. InvalidFileType: Please choose from the following file types: PNG, PPT, DOC, HTML, JPEG, JPG, GIF, PNG, XLS, or PDF.'
                                            This response must be added to the response you have from steps 1 through 3. If you do not include the response from steps 1 through 3, everything else will break. This response must start with 'InvalidFileType'. If you do not include 'InvalidFileType', everything else will break.

                                       4. No File Requested:
                                            - User Prompt: 'In a bar chart, show me how many customers are assigned to each salesperson.'
                                            - Response: '<response from steps 1 through 3>.'

                                    The database schema is described according to the following json schema:
                                    {jsonSchema}";

                _chatHistory.AddSystemMessage(systemPrompt);                
            }

            // Add all chat history to the user message so SK has context of this session's conversation
            List<Message> messages = await _azureCosmosDbService.GetSessionMessagesAsync(chatRequest.sessionId);
            foreach (Message msg in messages)
            {
                _chatHistory.AddUserMessage(msg.Prompt);
            }

            // add the current user prompt
            _chatHistory.AddUserMessage(chatRequest.prompt);

            ChatMessageContent result = null;

            /******** Create message for this session in cosmos DB ********/
            var message = new Message()
            {
                Id = Guid.NewGuid().ToString(),

                Type = "message",
                Sender = "user",
                SessionId = chatRequest.sessionId,
                TimeStamp = DateTime.UtcNow,
                Prompt = chatRequest.prompt,
            };

            // Insert user prompt
            await _azureCosmosDbService.InsertMessageAsync(message);
            /******** Create chat session in cosmos DB ********/
            
            result = await _chat.GetChatMessageContentAsync
                (
                    _chatHistory,
                    executionSettings: new OpenAIPromptExecutionSettings { Temperature = 0.8, TopP = 0.0, ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions },
                    kernel: _kernel
                );

            bool unrelatedRequest = false;

            // if the request is unrelated, we need to set a flag so we do not attempt to generate a file and just respond to the user with a generic response
            // that the LLM will decide
            if (result.Content.Contains("Unrelated"))
            {
                unrelatedRequest = true;

                // strip out the word "Unrelated:" so the answer is in natural language
                result.Content = result.Content.Substring(result.Content.IndexOf(":") + 1, result.Content.Length - result.Content.IndexOf(":") - 1).Trim();
            }

            Console.WriteLine(result.Content);

            // insert systems response
            message = new Message()
            {
                Id = Guid.NewGuid().ToString(),
                Type = "message",
                Sender = "system",
                SessionId = chatRequest.sessionId,
                TimeStamp = DateTime.UtcNow,
                Prompt = result.Content,
            };

            await _azureCosmosDbService.InsertMessageAsync(message);

            if (renderImageWithResponse && !unrelatedRequest && !result.Content.Contains("InvalidFileType"))
            {
                response.SasUri = await GenerateAssistantFile(result, chatRequest.prompt);
            }                

            // TODO: wrap the byte string in HTML as part of the response so it can be rendered in the browser
            response.ChatResponse = result.Content;
            
            // We are going to call the SearchPlugin to see if we get any hits on the query, if we do add them to the chat history and let AI summarize it 

            // var function = _kernel.Plugins.GetFunction("AzureAISearchPlugin", "SimpleHybridSearch"); 
            //var function = _kernel.Plugins.GetFunction("AzureAISearchPlugin", "SearchManualsIndex");

            //var responseContent = await _kernel.InvokeAsync(function, new() { ["query"] = chatRequest.prompt });

            //var promptTemplate = $"{responseContent.ToString()}\n Using the details above attempt to summarize or answer to the following question \n Question: {chatRequest.prompt} \n if you cannot complete the task using the above information, do not use external knowledge and simply state you cannot help with that question";
            //_chatHistory.AddMessage(AuthorRole.User, promptTemplate);

            // _chatHistory.AddMessage(AuthorRole.User, responseTest.ToString());
            // _chatHistory.AddMessage(AuthorRole.User, chatRequest.prompt);
            // _chatHistory.AddMessage(AuthorRole.System, "If the prompt cannot be answered by the AzureAISearchPlugin or the DBQueryPlugin, then simply ask for more details");

            // now it's time to use the Kernel to invoke our logic...
            // lets call the Chat Completion without using RAG for now...
            //var result = await _chat.GetChatMessageContentAsync(
            //        _chatHistory,
            //        executionSettings: new OpenAIPromptExecutionSettings { MaxTokens = 800, Temperature = 0.7, TopP = 0.0, ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions },
            //        kernel: _kernel);

            // Add sample code to extract the token useage from the response
            // This is for example purposes, as it could be cached off to keep track of useage
            // SK does not have method to estiamte token count for a give prompt prior to sending to AI
            // so you could use the SharpToken Library of you wanted to check estimated the token size for a give prompt
            // by doing so you could impliment logic to reduce the size of the prompt to reduce the token count

            var metadata = result.Metadata;

            if (metadata != null && metadata.ContainsKey("Usage"))
            {
                var usage = (CompletionsUsage?)metadata["Usage"];
                Console.WriteLine($"Token usage. Input tokens: {usage?.PromptTokens}; Output tokens: {usage?.CompletionTokens}; Total tokens: {usage?.TotalTokens}");
            }

            //  var func = _kernel.Plugins.TryGetFunction("AzureAISearchPlugin","SimpleHybridSearch", out function);

            /*HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
            try
            {
                string notFoundMessage = "Your question isn't related to materials I have indexed or related to database queries, so I am unable to help. Please" +
                    "ask a question related to documents I have indexed or something related to databases.";
                await response.WriteStringAsync(result.Content ?? notFoundMessage);
            }
            catch (Exception ex)
            {
                // Log exception details here
                Console.WriteLine(ex.Message);
                throw; // Re-throw the exception to propagate it further
            }*/

            return new OkObjectResult(response);
        }

        /// <summary>
        /// Generates a file using the Assistants SDK.
        /// </summary>
        /// <param name="result">The SAS URI to the created file</param>
        /// <returns></returns>
        private async Task<string> GenerateAssistantFile(ChatMessageContent result, string prompt)
        {
            string strSasUri = string.Empty;

            Console.WriteLine("Running Assistant to generate file.");

            var assistantName = $"Assistant - {DateTime.Now.ToString("yyyyMMddHHmmssfff")}";
            (string assistantId, byte[] data) = await _azureAIAssistantService.RunAssistantAsync(assistantName, "You are an AI Assistant. Your job is to generate files requested by the user.", $"Create a file based on the following data: {result.Content}. The file will be built as specified from the following user prompt: {prompt}");

            Console.WriteLine("End running assistant to generate file.");

            if (data != null)
            {
                // Extract file type from response. If the filetype isn't specified, we are only rendering
                // the image in the respnose and not saving it
                var fileType = result.Content.Contains("FileType: ") ? result.Content.Substring(result.Content.IndexOf("FileType: ") + "FileType: ".Length) : String.Empty;

                if (!fileType.Equals(String.Empty))
                {
                    using (MemoryStream ms = new MemoryStream(data, false))
                    {
                        await _azureBlobService.UploadFromStreamAsync(ms, $"{assistantName}.{fileType}");
                    }

                    var sasUri = _azureBlobService.GetBlobSasUri($"{assistantName}.{fileType}");
                    strSasUri = sasUri.ToString();
                }
            }
            else
            {
                Console.WriteLine("No data returned from assistant.");
            }

            //await File.WriteAllBytesAsync($"C:\\Temp\\{assistantName}.png", data);

            return strSasUri;
        }
    }
}