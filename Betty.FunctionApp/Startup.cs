using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

[assembly: WebJobsStartup(typeof(Betty.FunctionApp.Startup))]
namespace Betty.FunctionApp
{
    public class Startup : IWebJobsStartup
    {
        public void Configure(IWebJobsBuilder builder)
        {
            builder.Services.AddLogging(c =>
            {
                c.AddSentry(Environment.GetEnvironmentVariable("SENTRY_DSN"));
            });
        }
    }
}
