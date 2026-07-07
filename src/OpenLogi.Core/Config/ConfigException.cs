namespace OpenLogi.Core.Config;

/// <summary>Raised when config I/O or parsing fails.</summary>
public sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);
