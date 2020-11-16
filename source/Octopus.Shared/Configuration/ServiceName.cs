using System;
using Octopus.Shared.Configuration.Instances;

namespace Octopus.Shared.Configuration
{
    public static class ServiceName
    {
        public static string GetWindowsServiceName(ApplicationName application, string instanceName)
        {
            string? name;

            switch (application)
            {
                case ApplicationName.OctopusServer:
                    name = "OctopusDeploy";
                    break;
                case ApplicationName.Tentacle:
                    name = "OctopusDeploy Tentacle";
                    break;
                default:
                    throw new ArgumentException("Invalid application name", nameof(application));
            }

            var defaultInstanceName = ApplicationInstanceRecord.GetDefaultInstance(application);
            if (defaultInstanceName == instanceName)
                return name;

            return name + ": " + instanceName;
        }
    }
}