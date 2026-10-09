// Copied from Chapter 23 (Ch23.Beacon.Security).
using System.Security.Cryptography;
using Microsoft.Extensions.Compliance.Classification;
using Microsoft.Extensions.Compliance.Redaction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ch27.Beacon.Agent.Security.Logging;

public static class BeaconRedaction
{
    /// <summary>
    /// Turns on redaction for classified log parameters: personal data becomes a keyed hash (the same
    /// user hashes the same way, so you can still follow one person's requests), and conversation
    /// content is erased. Only parameters that carry a classification are touched.
    /// </summary>
    public static IHostApplicationBuilder AddBeaconRedaction(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // In production the key comes from a secret store and is rotated (the key ID says which one
        // produced a hash). Without one, a random per-process key still hides IDs, but hashes only
        // correlate within one run of the server.
        var key = builder.Configuration["Beacon:Redaction:HmacKey"];
        if (string.IsNullOrWhiteSpace(key))
        {
            key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        }

        builder.Logging.EnableRedaction();
        builder.Services.AddRedaction(redaction =>
        {
            redaction.SetHmacRedactor(
                options =>
                {
                    options.Key = key;
                    options.KeyId = 2301;
                },
                new DataClassificationSet(BeaconTaxonomy.PersonalData));

            redaction.SetRedactor<ErasingRedactor>(new DataClassificationSet(BeaconTaxonomy.ConversationContent));
        });

        return builder;
    }
}
