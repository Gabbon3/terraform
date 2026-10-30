using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length != 2)
{
    Console.Error.WriteLine("Uso: SchemaGenerator <assembly-terraform> <actions.schema.json>");
    return 2;
}

try
{
    var assemblyPath = Path.GetFullPath(args[0]);
    var outputPath = Path.GetFullPath(args[1]);
    if (!File.Exists(assemblyPath))
    {
        throw new FileNotFoundException($"Assembly Terraform non trovato: {assemblyPath}", assemblyPath);
    }

    AssemblyLoadContext.Default.Resolving += (_, name) =>
    {
        var sibling = Path.Combine(Path.GetDirectoryName(assemblyPath)!, $"{name.Name}.dll");
        return File.Exists(sibling) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(sibling) : null;
    };
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
    var catalogType = assembly.GetType("terraform.lib.engine.ActionCatalog", throwOnError: true)!;
    var create = catalogType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("ActionCatalog.Create non trovato.");
    var actions = create.Invoke(null, [null]) as IEnumerable
        ?? throw new InvalidOperationException("ActionCatalog.Create non ha restituito un catalogo.");
    var schemas = new List<object>();

    foreach (var action in actions)
    {
        if (action is null)
            throw new InvalidOperationException("ActionCatalog contiene un'action nulla.");

        var actionType = action.GetType();
        var typeName = ReadString(actionType.GetProperty("ActionType")?.GetValue(action), $"{actionType.Name}.ActionType");
        var metadata = actionType.GetCustomAttributes(false).SingleOrDefault(a => a.GetType().FullName == "terraform.lib.engine.actions.ActionMetadataAttribute")
            ?? throw new InvalidOperationException($"{typeName}: manca ActionMetadataAttribute con la descrizione dell'action.");
        var description = ReadString(metadata.GetType().GetProperty("Description")?.GetValue(metadata), $"{typeName}: descrizione");
        var configAttribute = actionType.GetCustomAttributes(false).SingleOrDefault(a => a.GetType().FullName == "terraform.lib.engine.actions.ActionConfigAttribute")
            ?? throw new InvalidOperationException($"{typeName}: manca ActionConfigAttribute.");
        var configType = configAttribute.GetType().GetProperty("ConfigType")?.GetValue(configAttribute) as Type
            ?? throw new InvalidOperationException($"{typeName}: il tipo config non è stato dichiarato.");

        schemas.Add(new Dictionary<string, object?>
        {
            ["type"] = typeName,
            ["description"] = description,
            ["schema"] = BuildObjectSchema(configType, typeName)
        });
    }

    if (schemas.Count == 0)
        throw new InvalidOperationException("ActionCatalog non contiene action.");

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["title"] = "Terraform action catalog",
        ["actions"] = schemas
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Schema generato per {schemas.Count} action: {outputPath}");
    return 0;
}
catch (Exception ex)
{
    var error = ex is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException! : ex;
    Console.Error.WriteLine($"Generazione schema fallita: {error.Message}");
    return 1;
}

static Dictionary<string, object?> BuildObjectSchema(Type configType, string actionName)
{
    var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
    var required = new List<string>();
    foreach (var property in configType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
    {
        if (property.GetIndexParameters().Length != 0)
            continue;
        var attribute = property.GetCustomAttributes(false).SingleOrDefault(a => a.GetType().FullName == "terraform.lib.engine.actions.ConfigFieldAttribute")
            ?? throw new InvalidOperationException($"{actionName}.{property.Name}: manca ConfigFieldAttribute/descrizione.");
        var description = ReadString(attribute.GetType().GetProperty("Description")?.GetValue(attribute), $"{actionName}.{property.Name}: descrizione");
        var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
        properties[name] = BuildPropertySchema(property, attribute, $"{actionName}.{name}");
        if (Read<bool>(attribute, "Required"))
            required.Add(name);
    }

    if (properties.Count == 0)
        throw new InvalidOperationException($"{actionName}: il tipo config {configType.FullName} non espone proprietà supportate.");

    var schema = new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["additionalProperties"] = true
    };
    if (required.Count > 0)
        schema["required"] = required;
    return schema;
}

static Dictionary<string, object?> BuildPropertySchema(PropertyInfo property, object attribute, string field)
{
    var description = ReadString(attribute.GetType().GetProperty("Description")?.GetValue(attribute), $"{field}: descrizione");
    var schema = BuildTypeSchema(property.PropertyType, field, attribute);
    schema["description"] = description;

    var defaultJson = Read<string?>(attribute, "DefaultJson");
    if (defaultJson is not null)
    {
        try
        {
            schema["default"] = JsonDocument.Parse(defaultJson).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{field}: default JSON non valido: {ex.Message}");
        }
    }

    var minimum = Read<int>(attribute, "Minimum");
    var maximum = Read<int>(attribute, "Maximum");
    if (minimum != int.MinValue) schema["minimum"] = minimum;
    if (maximum != int.MaxValue) schema["maximum"] = maximum;

    var ui = new Dictionary<string, object?>();
    if (Read<bool>(attribute, "Sensitive")) ui["sensitive"] = true;
    var widget = Read<string?>(attribute, "Widget");
    if (widget is not null) ui["widget"] = widget;
    var requiredWhen = property.GetCustomAttributes(false).FirstOrDefault(a => a.GetType().FullName == "terraform.lib.engine.actions.RequiredWhenAttribute");
    if (requiredWhen is not null)
    {
        ui["requiredWhen"] = new Dictionary<string, object?>
        {
            ["property"] = JsonNamingPolicy.CamelCase.ConvertName(ReadString(requiredWhen.GetType().GetProperty("Property")?.GetValue(requiredWhen), field)),
            ["notEqual"] = ReadString(requiredWhen.GetType().GetProperty("NotEqual")?.GetValue(requiredWhen), field)
        };
    }
    if (ui.Count > 0) schema["x-ui"] = ui;
    return schema;
}

static Dictionary<string, object?> BuildTypeSchema(Type type, string field, object attribute)
{
    type = Nullable.GetUnderlyingType(type) ?? type;
    if (type == typeof(JsonElement))
    {
        var itemType = attribute.GetType().GetProperty("ItemType")?.GetValue(attribute) as Type;
        if (itemType is null)
            throw new InvalidOperationException($"{field}: JsonElement richiede un ItemType dichiarato.");
        var variants = (string[]?)attribute.GetType().GetProperty("Variants")?.GetValue(attribute) ?? [];
        if (variants.Length == 0)
            throw new InvalidOperationException($"{field}: tipo JsonElement senza varianti supportate.");
        return new Dictionary<string, object?>
        {
            ["oneOf"] = variants.Select(variant => variant switch
            {
                "string" => new Dictionary<string, object?> { ["type"] = "string" },
                "object" => BuildObjectSchema(itemType, field),
                _ => throw new InvalidOperationException($"{field}: variante non supportata '{variant}'.")
            }).ToArray()
        };
    }

    if (type == typeof(string))
    {
        var values = (string[]?)attribute.GetType().GetProperty("EnumValues")?.GetValue(attribute);
        return values is { Length: > 0 }
            ? new Dictionary<string, object?> { ["type"] = "string", ["enum"] = values }
            : new Dictionary<string, object?> { ["type"] = "string" };
    }
    if (type == typeof(bool)) return new() { ["type"] = "boolean" };
    if (type.IsEnum) return new() { ["type"] = "string", ["enum"] = Enum.GetNames(type) };
    if (type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long))
        return new() { ["type"] = "integer" };
    if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
        return new() { ["type"] = "number" };

    if (type.IsGenericType && typeof(IDictionary).IsAssignableFrom(type))
    {
        var valueType = type.GetGenericArguments()[1];
        return new()
        {
            ["type"] = "object",
            ["additionalProperties"] = BuildTypeSchema(valueType, $"{field} value", attribute)
        };
    }
    if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
    {
        var elementType = type.GetGenericArguments()[0];
        return new() { ["type"] = "array", ["items"] = BuildTypeSchema(elementType, $"{field} item", attribute) };
    }
    if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
        return BuildObjectSchema(type, field);

    throw new InvalidOperationException($"{field}: tipo .NET non supportato '{type.FullName}'.");
}

static T Read<T>(object instance, string propertyName)
{
    var value = instance.GetType().GetProperty(propertyName)?.GetValue(instance);
    return value is null ? default! : (T)value;
}

static string ReadString(object? value, string field) =>
    value is string text && !string.IsNullOrWhiteSpace(text)
        ? text
        : throw new InvalidOperationException($"{field}: valore obbligatorio mancante.");
