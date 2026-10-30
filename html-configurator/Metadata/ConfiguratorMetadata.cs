namespace terraform.lib.engine.actions;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ActionMetadataAttribute(string description) : Attribute
{
    public string Description { get; } = description;
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ActionConfigAttribute(Type configType) : Attribute
{
    public Type ConfigType { get; } = configType;
}

[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class ConfigFieldAttribute(string description) : Attribute
{
    public string Description { get; } = description;
    public bool Required { get; set; }
    public string? DefaultJson { get; set; }
    public int Minimum { get; set; } = int.MinValue;
    public int Maximum { get; set; } = int.MaxValue;
    public bool Sensitive { get; set; }
    public Type? ItemType { get; set; }
    public string[] Variants { get; set; } = [];
    public string[]? EnumValues { get; set; }
    public string? Widget { get; set; }
}

[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class RequiredWhenAttribute(string property, string notEqual) : Attribute
{
    public string Property { get; } = property;
    public string NotEqual { get; } = notEqual;
}
