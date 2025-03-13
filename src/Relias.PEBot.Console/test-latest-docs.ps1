#!/usr/bin/env pwsh

# Script to test the latest Confluence docs functionality using the console application
Write-Host "Testing latest Confluence docs functionality..." -ForegroundColor Cyan

# Navigate to the console application directory
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptPath

# Run the console application with the test-confluence command
dotnet run -- --test-confluence "latest 5 docs"

Write-Host "Test execution completed." -ForegroundColor Green 