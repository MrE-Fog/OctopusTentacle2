using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Octopus.Shared.Util;

namespace Octopus.Shared.Contracts
{
    public class StartScriptCommand
    {
        [JsonConstructor]
        public StartScriptCommand(string scriptBody, ScriptIsolationLevel isolation, TimeSpan scriptIsolationMutexTimeout, string[] arguments, string taskId)
        {
            Arguments = arguments;
            TaskId = taskId;
            ScriptBody = scriptBody;
            Isolation = isolation;
            ScriptIsolationMutexTimeout = scriptIsolationMutexTimeout;
        }

        public StartScriptCommand(string scriptBody, ScriptIsolationLevel isolation, TimeSpan scriptIsolationMutexTimeout, string[] arguments, string taskId, params ScriptFile[] additionalFiles)
            : this(scriptBody, isolation, scriptIsolationMutexTimeout, arguments, taskId)
        {
            if (additionalFiles != null)
            {
                Files.AddRange(additionalFiles);
            }
        }

        public StartScriptCommand(string scriptBody, ScriptIsolationLevel isolation, TimeSpan scriptIsolationMutexTimeout, string[] arguments, string taskId, Dictionary<ScriptType, string> additionalScripts, params ScriptFile[] additionalFiles)
            : this(scriptBody, isolation, scriptIsolationMutexTimeout, arguments, taskId, additionalFiles)
        {
            if (!additionalScripts.IsNullOrEmpty())
            {
                Scripts.AddRange(additionalScripts);
            }
        }

        public string ScriptBody { get; }

        public ScriptIsolationLevel Isolation { get; }

        private Dictionary<ScriptType, string> Scripts { get; } = new Dictionary<ScriptType, string>();

        public List<ScriptFile> Files { get; } = new List<ScriptFile>();

        public string[] Arguments { get; }

        public string TaskId { get; }

        public TimeSpan ScriptIsolationMutexTimeout { get; }
    }
}