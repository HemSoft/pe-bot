#!/bin/bash

# Script to run the latest Confluence docs integration test
echo -e "\033[0;36mRunning integration test for latest Confluence docs...\033[0m"

# Navigate to the integration tests directory
SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
cd "$SCRIPT_DIR"

# Run the specific test
dotnet test --filter "FullyQualifiedName=Relias.PEBot.IntegrationTests.AssistantClientIntegrationTests.SearchConfluence_ShouldReturnLatestFiveDocuments" -v normal

echo -e "\033[0;32mTest execution completed.\033[0m" 