#!/bin/bash

# Script to test the latest Confluence docs functionality using the console application
echo -e "\033[0;36mTesting latest Confluence docs functionality...\033[0m"

# Navigate to the console application directory
SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
cd "$SCRIPT_DIR"

# Run the console application with the test-confluence command
dotnet run -- --test-confluence "latest 5 docs"

echo -e "\033[0;32mTest execution completed.\033[0m" 