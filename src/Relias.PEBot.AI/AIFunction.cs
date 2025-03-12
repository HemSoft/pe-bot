namespace Relias.PEBot.AI;

using System;
using System.Threading.Tasks;

public class AIFunction
{
    public string Name { get; }
    public string Description { get; }
    public object Parameters { get; }
    private readonly Func<string, Task<string>> _invokeFunction;

    public AIFunction(string name, string description, object parameters, Func<string, Task<string>> invokeFunction)
    {
        Name = name;
        Description = description;
        Parameters = parameters;
        _invokeFunction = invokeFunction;
    }

    public async Task<string> InvokeAsync(string arguments)
    {
        return await _invokeFunction(arguments);
    }
}