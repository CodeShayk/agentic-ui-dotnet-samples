using Yarp.ReverseProxy.Transforms;

namespace Ch24.Proxy;

// A reverse proxy in front of Beacon's agent, to see what proxies do to an AG-UI stream.
//   /direct/agent      streams through; YARP cancels a request that is idle longer than the
//                      cluster's HttpRequest:ActivityTimeout (100 seconds unless configured)
//   /inspected/agent   holds each response until it is complete, to inspect it, as any proxy that
//                      scans or rewrites response bodies must
public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddReverseProxy()
            .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
            .AddTransforms(context =>
            {
                if (context.Route.Metadata?.TryGetValue("InspectResponseBody", out var inspect) == true && inspect == "true")
                {
                    context.AddResponseTransform(InspectWholeBodyAsync);
                }
            });

        var app = builder.Build();
        app.MapReverseProxy();
        app.Run();
    }

    // Reading the body to the end before forwarding any of it is what scanning requires, and it is
    // exactly what turns a stream into one late response.
    private static async ValueTask InspectWholeBodyAsync(ResponseTransformContext context)
    {
        if (context.ProxyResponse is null)
        {
            return;
        }

        var aborted = context.HttpContext.RequestAborted;
        var body = await context.ProxyResponse.Content.ReadAsByteArrayAsync(aborted);

        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Inspected {Bytes} bytes of {Path} before forwarding them.", body.Length, context.HttpContext.Request.Path);

        context.SuppressResponseBody = true;
        context.HttpContext.Response.ContentLength = body.Length;
        await context.HttpContext.Response.Body.WriteAsync(body, aborted);
    }
}
