# Changelog

All notable changes to the Relias PE-Bot project will be documented in this file.

## 2024-08-22

### Changed
- Sorted using statements alphabetically in all source files
- Added missing System.IO using statement in Program.cs
- Ensured all using statements are placed after namespace declarations

## 2024-08-21

### Changed
- Extracted AssistantFile class into its own file from AssistantClient.cs
- Created new AssistantManager class to handle assistant-related operations
- Created new AssistantRunManager class to handle run and thread operations
- Added interfaces IAssistantManager and IAssistantRunManager for better dependency injection support
- Improved code organization by splitting large files into more focused components

## 2024-08-20

### Fixed
- Updated ProcessFunctionCallsAsync to directly check run's required_action for tool calls
- Fixed tool call extraction logic to properly handle Confluence function calls
- Improved tool output handling to prevent empty responses
- Added better logging for tool call processing flow

## 2024-08-19

### Changed
- Updated system prompt to better handle Confluence integration's development status
- Simplified SearchConfluence method to consistently return implementation pending message

### Fixed
- Removed duplicate Confluence function registration from Program.cs since they are already registered in AssistantClient constructor
- Fixed AIFunctionFactory to properly preserve function responses including "Implementation pending" messages
- Added detailed logging in AIFunctionFactory for better debugging of function invocations
- Improved error handling in AIFunctionFactory to differentiate between JSON parsing and function invocation errors
- Updated GetResponseAsync in AssistantClient to properly include all tools in run requests
- Fixed run creation to combine registered functions with file_search tool
- Added logging to show number of tools included in run requests

## 2024-08-18

### Fixed
- Fixed Confluence function callbacks in AssistantClient to properly handle implementation pending messages
- Improved function execution logging to better diagnose tool call issues
- Enhanced function argument handling in ProcessFunctionCallsAsync
- Added detailed logging for function call flow and responses

## 2024-08-17

### Fixed
- Fixed compiler warnings in `ConfluenceInfoProvider.cs` by properly implementing async patterns with `await` operators
- Added proper Console logging to all Confluence methods to improve troubleshooting
- Ensured consistent error handling patterns across all Confluence integration methods

## 2024-08-16

### Changed
- Updated `ConfluenceInfoProvider.cs` to consistently use the `ImplementationPendingMessage` constant for better user experience
- Ensured all Confluence API methods return consistent implementation pending messages

## 2024-08-15

### Fixed
- Enhanced `ProcessFunctionCallsAsync` in AssistantClient to better handle empty responses from Confluence tools
- Added new `GetFriendlyErrorMessageForFunction` method that provides helpful user-facing messages
- Improved error detection for "Implementation pending" and empty response conditions
- Added function-specific error messages for different Confluence API calls
- Improved error logging for tool call processing

### Changed
- Updated `ConfluenceInfoProvider.cs` with better error handling and user-friendly messages
- Added const values for reusable error messages in Confluence provider
- Applied code guidelines including proper error handling in Confluence methods

## 2024-08-14

### Fixed
- Identified empty response handling in Confluence integration with SearchConfluence and GetRelatedPages functions
- Added error handling for Confluence tool calls that return no data
- Improved fallback responses when Confluence search returns no results

## 2024-08-13

### Fixed
- Identified issue with Confluence integration where placeholder implementations are returning "Implementation pending" messages
- Diagnosed function call handling in Assistant API when Confluence functions are called but not fully implemented
- Improved error handling for empty tool call responses

## 2024-08-12

### Fixed
- Fixed handling of "requires_action" status in AssistantClient when no tool outputs are available
- Added fallback mechanism to properly handle function calls when the function isn't registered
- Improved error recovery for tool calls in the Azure OpenAI assistant integration
- Enhanced logging for tool call processing to better diagnose issues

## [Unreleased]

### Added
- Added new `PEAssistantClient` class that provides an alternative implementation using Azure's Assistants API
- Implemented exponential backoff for polling Assistant API runs to prevent rate limiting
- Updated Slack integration to support both AssistantClient and ChatClient with ability to switch between them
- Added logging to show which client type is being used in Slack integration
- Added vector store retrieval capability to AssistantClient to access documents attached to the Azure OpenAI Assistant
- Added file upload and attachment features to AssistantClient to enable document access
- Added configuration support for loading file IDs from the app settings
- Added error recovery logic to handle cases where the assistant ID doesn't exist or is inaccessible
- Added support for Azure AD (Entra ID) authentication as an alternative to API key authentication
- Added tool_resources support with vector store IDs for improved file search capabilities
- Added new method GetAssistantVectorStoresAsync() to retrieve vector stores attached to an assistant
- Added Azure.Identity package reference to support DefaultAzureCredential for Entra ID authentication
- Added Microsoft.Extensions.AI package reference to support ChatMessage and ChatRole types in both clients
- Added comprehensive configuration validation in Program.cs to check for required settings
- Added multiple configuration sources with proper precedence (appsettings.json, environment variables, user secrets)

### Changed
- Modified Program.cs to use AssistantClient instead of PEChatClient for Slack integration
- Updated MessageEventHandler to properly handle both client types with priority given to AssistantClient
- Improved code organization following style guidelines (const strings, readonly fields)
- Updated AssistantClient to use the newer file_search tool instead of the deprecated retrieval tool
- Optimized AssistantClient code with additional const values and LINQ for tool configuration
- Enhanced AssistantClient to properly attach files to assistants during creation
- Improved assistant verification process to create a new assistant when the existing one cannot be found
- Updated API version from 2024-02-15-preview to 2024-05-01-preview for better compatibility
- Refactored authentication method to support both API key and Azure AD token-based authentication
- Reorganized using statements in AssistantClient.cs to follow alphabetical order according to coding guidelines
- Updated ChatClient.cs to correctly use UseFunctionInvocation with Microsoft.Extensions.AI 9.3.0-preview API

### Fixed
- Fixed null reference warnings in AssistantClient by adding proper null handling for JSON properties
- Made AssistantFile properties non-nullable with default empty strings
- Fixed chat message creation in ChatClient to use concrete ChatRequestMessage types from Azure.AI.OpenAI SDK beta.13
- Improved type safety by using explicit namespaces for ChatRole values
- Updated ChatClient.cs to use specific ChatRequestMessage types (SystemMessage, UserMessage, AssistantMessage) for compatibility with Azure.AI.OpenAI beta.13
- Fixed type conversion issues between Microsoft.Extensions.AI.ChatRole and Azure.AI.OpenAI roles
- Resolved naming conflicts by using explicit namespaces and types
- Fixed chat message creation to use the correct SDK classes
- Updated ChatClient.cs to use OpenAIClient instead of AzureOpenAIClient for compatibility with latest Azure.AI.OpenAI SDK
- Fixed ChatMessage creation to use new ChatRequestMessage format
- Updated GetChatCompletionsAsync method to use new API signature with DeploymentName in options
- Fixed AI message handling in MessageEventHandler to properly send messages to the ChatClient
- Improved message response handling in Slack integration
- Added error handling for AI message processing
- Updated AI message detection to correctly handle format "<USERID> AI Real message here."
- Modified text extraction logic to properly parse messages with "AI" prefix
- Removed unused GetAIResponse() method
- Updated using statement order to follow alphabetical sorting guidelines
- Corrected namespace references in AssistantClient.cs to fix build errors
- Fixed implementation issues in AssistantClient.cs to properly use the Azure OpenAI SDK
- Implemented direct HTTP calls in AssistantClient.cs to avoid SDK version compatibility issues
- Improved error handling in AssistantClient.cs with more descriptive error messages
- Removed unused namespaces from Program.cs
- Fixed 404 "Resource not found" error in AssistantClient.cs by updating API endpoint paths to use correct Azure OpenAI Assistants API format
- Added API version parameter to AssistantClient.cs requests to ensure compatibility with Azure OpenAI Assistants API
- Fixed issue in AssistantClient where vector store access was not enabled, preventing document retrieval in assistant responses
- Fixed BadRequest error by updating AssistantClient to use file_search tool instead of deprecated retrieval tool
- Fixed document retrieval issue by adding proper file attachment and management functionality
- Fixed critical 404 error when attempting to update assistant tools by adding graceful error handling and fallback mechanism
- Addressed authentication issues by adding support for Azure AD token-based authentication
- Fixed build error with missing Azure.Identity namespace by adding the required package reference
- Fixed build error with missing Microsoft.Extensions.AI namespace by adding the required package reference
- Fixed IServiceProvider error with UseFunctionInvocation method by updating method call order for compatibility with Microsoft.Extensions.AI 9.3.0-preview