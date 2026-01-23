namespace Pole.Server.Features.Common.Exeptions;

public class ConflictExeption(string message) : InvalidOperationException(message);