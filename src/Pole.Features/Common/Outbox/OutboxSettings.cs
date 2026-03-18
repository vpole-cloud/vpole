namespace Pole.Features.Common.Outbox;

public class OutboxSettings
{
    public static string SectionName = "Outbox";
    
    public string ProcessingCronExpression { get; set; } = "*/15 * * * * *"; // Every 15 seconds
    
    public int RetentionDays { get; set; } = 7;
    
    public int MaxRetryAttempts { get; set; } = 5;
}