namespace Relias.PEBot.AI;

using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

public static class AIFunctionFactory
{
    public static AIFunction Create<T>(T target) where T : Delegate
    {
        var method = target.Method;
        var attributes = method.GetCustomAttributes();
        
        // Get the description from the DescriptionAttribute
        var description = attributes
            .OfType<DescriptionAttribute>()
            .FirstOrDefault()?.Description ?? method.Name;
        
        // Get parameter descriptions
        var parameters = new
        {
            type = "object",
            properties = method.GetParameters()
                .ToDictionary(
                    p => p.Name ?? string.Empty,
                    p => new
                    {
                        type = "string",
                        description = p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? p.Name
                    }
                ),
            required = method.GetParameters().Select(p => p.Name).Where(n => n != null).ToArray()
        };

        // Create wrapper function that handles JSON deserialization
        async Task<string> InvokeWrapper(string arguments)
        {
            try
            {
                Console.WriteLine($"Invoking {method.Name} with arguments: {arguments}");
                var jsonDoc = JsonDocument.Parse(arguments);
                var root = jsonDoc.RootElement;
                
                var paramValues = method.GetParameters()
                    .Select(p => {
                        var name = p.Name ?? throw new InvalidOperationException("Parameter name cannot be null");
                        Console.WriteLine($"Extracting parameter {name} from arguments");
                        return root.GetProperty(name).GetString();
                    });

                Console.WriteLine($"Calling {method.Name} with parameters: {string.Join(", ", paramValues)}");
                var result = await (Task<string>)target.DynamicInvoke(paramValues.ToArray())!;
                Console.WriteLine($"Function {method.Name} returned: {result}");
                
                // Preserve the result even if it's an implementation pending message
                return result;
            }
            catch (JsonException ex)
            {
                var error = $"Error parsing arguments for {method.Name}: {ex.Message}";
                Console.WriteLine(error);
                return error;
            }
            catch (Exception ex)
            {
                var error = $"Error invoking {method.Name}: {ex.Message}";
                Console.WriteLine($"{error}\nStack trace: {ex.StackTrace}");
                return error;
            }
        }

        return new AIFunction(
            method.Name,
            description,
            parameters,
            InvokeWrapper);
    }
}