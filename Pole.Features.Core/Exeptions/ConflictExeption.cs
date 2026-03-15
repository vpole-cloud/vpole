namespace Pole.Features.Core.Exeptions;

public class ConflictExeption(string message) : InvalidOperationException(message);