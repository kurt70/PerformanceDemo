using System.Net.Http.Headers;
using System.Web.Http;
using Microsoft.Owin;
using Owin;

namespace Api.Framework
{
    // OWIN startup configuration for Web API 2.
    public sealed class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            // Custom middleware that creates a manual server span per request.
            app.Use<ServerSpanMiddleware>();

            // Web API routing configuration.
            var config = new HttpConfiguration();
            config.MapHttpAttributeRoutes();
            config.Routes.MapHttpRoute(
                name: "DefaultApi",
                routeTemplate: "api/{controller}/{id}",
                defaults: new { id = RouteParameter.Optional });

            // Force JSON responses for consistency in perf tests.
            config.Formatters.JsonFormatter.SupportedMediaTypes.Add(
                new MediaTypeHeaderValue("application/json"));

            app.UseWebApi(config);
        }
    }
}
