﻿using System.Collections.Generic;
using System.Linq;
using Octopus.Shared.Configuration.EnvironmentVariableMappings;
using Octopus.Shared.Util;

namespace Octopus.Shared.Configuration.Instances
{
    public class EnvFileInstanceStrategy : IApplicationInstanceStrategy
    {
        readonly StartUpInstanceRequest startUpInstanceRequest;
        readonly IOctopusFileSystem fileSystem;
        readonly IEnvFileLocator envFileLocator;
        readonly IMapEnvironmentVariablesToConfigItems mapper;

        public EnvFileInstanceStrategy(StartUpInstanceRequest startUpInstanceRequest, IOctopusFileSystem fileSystem, IEnvFileLocator envFileLocator, IMapEnvironmentVariablesToConfigItems mapper)
        {
            this.startUpInstanceRequest = startUpInstanceRequest;
            this.fileSystem = fileSystem;
            this.envFileLocator = envFileLocator;
            this.mapper = mapper;
        }

        public int Priority => 300;

        public bool AnyInstancesConfigured()
        {
            return startUpInstanceRequest is StartUpDynamicInstanceRequest && envFileLocator.LocateEnvFile() != null;
        }

        public IList<ApplicationInstanceRecord> ListInstances()
        {
            if (!AnyInstancesConfigured())
                return Enumerable.Empty<ApplicationInstanceRecord>().ToList();
            return new List<ApplicationInstanceRecord>
            {
                new ApplicationInstanceRecord("EnvFile", true)
            };
        }

        public LoadedApplicationInstance LoadedApplicationInstance(ApplicationInstanceRecord applicationInstance)
        {
            return new LoadedApplicationInstance(applicationInstance.InstanceName, new EnvFileBasedKeyValueStore(fileSystem, envFileLocator, mapper));
        }
    }
}