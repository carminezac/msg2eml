using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MimeKit;

namespace Msg2Eml.Web;

/// <summary>
/// Checks the conversion prerequisites that may be missing in Linux containers:
/// the Windows code page provider (requirement 1 of the plan) and MimeKit serialization.
/// </summary>
public sealed class ConverterHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            _ = Encoding.GetEncoding(1252);
            _ = Encoding.GetEncoding("Windows-1252");

            var m = new MimeMessage { Subject = "health" };
            m.Body = new TextPart("plain") { Text = "ok" };
            m.Prepare(EncodingConstraint.SevenBit);
            using var ms = new MemoryStream();
            m.WriteTo(ms, cancellationToken);

            return Task.FromResult(HealthCheckResult.Healthy());
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("converter prerequisites missing", ex));
        }
    }
}
