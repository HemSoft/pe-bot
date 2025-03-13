#!/usr/bin/env pwsh

# Script to run the latest Confluence docs integration test
Write-Host "Running integration test for latest Confluence docs..." -ForegroundColor Cyan

# Navigate to the integration tests directory
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptPath

# Run the specific test
dotnet test --filter "FullyQualifiedName=Relias.PEBot.IntegrationTests.AssistantClientIntegrationTests.SearchConfluence_ShouldReturnLatestFiveDocuments" -v normal

Write-Host "Test execution completed." -ForegroundColor Green 