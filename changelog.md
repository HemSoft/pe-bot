# Changelog

All notable changes to the Relias PE-Bot project will be documented in this file.

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

### Fixed
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