### General

Update changelog.md with any significant changes. Don't bother with small fixes and updates.
Maintain a TODO.md with suggestions on what to improve. Also, if you implement any TODO items, move them to the DONE section.

### Syntax Errors

Make sure you do a dotnet build after each session to make sure your proposed changes are syntactically correct.

### Testing

Make sure to run all tests every time.

### Debugging and interating over issues.

Always check Output.txt for console output.
If you find any errors address one and return control to me to run the app which in turn will create a fresh copy of Output.txt that we can work on for next iteration.

### Code Guidelines

Always use file-scoped namespace declaration.
Always put using statements after the namespace declaration.
Sort using statements alphabetically.
Remove unnecessary using statements.
Use readonly local variables where possible.
Use const strings where possible.
Remove unused variables.
Investigate unused methods. Are they needed?
Don't embed class definitions or enumerations with code methods. Bring them into their own classes. Consider using an Enums and Models folder or similar names to put those extractions into.
If a class grows beyond 150-200 lines it is time to split out the functionality into multiple functional classes. Try to extract as much into logical components.

### Command Execution Guidelines

When constructring multiple commands to execute on the command line don't use && - use Semicolon as your delimiter instead.