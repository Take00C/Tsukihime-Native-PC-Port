namespace Tsuki.Data.Containers;

/// <summary>
/// A container, name table or compressed stream does not follow the documented layout
/// (docs/formats/containers.md). The message says which rule failed.
/// </summary>
public class ContainerFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ContainerFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public ContainerFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
