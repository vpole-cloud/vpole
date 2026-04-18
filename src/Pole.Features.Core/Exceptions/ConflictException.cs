namespace Pole.Features.Core.Exceptions;

public sealed class ConflictException(string message) : InvalidOperationException(message);