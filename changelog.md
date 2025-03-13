# Changelog

All notable changes to the Relias PE-Bot project will be documented in this file.

## [Unreleased]

### Added
- Added console output logging to Output.txt with MultiTextWriter implementation
- Added automatic file cleanup by overwriting Output.txt on each run
- Added relative path resolution to ensure Output.txt is created in project root
- Added detailed logging for tool call processing to aid in troubleshooting
- Added fallback mechanism for Confluence searches when the assistant run fails
- Added integration test for retrieving the 5 latest Confluence documents
- Added convenience scripts to run integration tests and manual tests

### Changed
- Removed the requirement for messages to be prefixed with "AI" to get a response from the bot
- Updated message handling to process all @mentions and direct messages with the AI client
- Simplified the user experience by removing the need for special prefixes
- Enabled Confluence integration by removing the "in development" notice from the system prompt
- Improved Confluence search handling with more specific instructions in the system prompt
- Fixed tool call handling in AssistantClientSdk to properly process Confluence search requests
- Enhanced error reporting for failed assistant runs
- Improved thread management by creating a new thread for each conversation
- Improved handling of "latest documents" requests to use the GetRecentUpdates function for more accurate results
- Enhanced GetRecentUpdates method to handle empty search terms, returning all recent documents when no specific topic is provided

### Fixed
- Fixed issue where the bot would incorrectly state that no Confluence documents were available
- Fixed tool call processing to properly handle the RequiresAction status from the Azure OpenAI API
- Fixed error handling in the AssistantClientSdk to provide more detailed error information
- Fixed thread management to ensure proper creation and usage of threads
- Added fallback response for failed runs to ensure users always receive a response
- Fixed Confluence search integration tests by improving result formatting and limiting
- Enhanced Confluence search detection to properly identify search requests
- Implemented proper result count limiting for Confluence searches based on user requests
- Fixed issue where requests for latest Confluence documents would return outdated documents instead of the most recently updated ones
- Fixed issue where generic requests for latest documents (without specifying a topic) would use inappropriate search terms

