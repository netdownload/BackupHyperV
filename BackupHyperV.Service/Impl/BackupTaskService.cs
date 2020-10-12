﻿using BackupHyperV.Service.Interfaces;
using BackupHyperV.Service.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SimpleSchedules;
using System;
using System.Collections.Generic;
using System.IO;
using System.Management;

/*
Work logic:
1. If central server was detected, try to download backup task from it.
2. Central server NOT detected, try to use local backup task.
3. No local backup task file was found? Try to query Hyper-V WMI API
   and create new BackupTask.json with all found virtual machines.
   Their schedules will be disabled and human must check this file
   before use.
*/

namespace BackupHyperV.Service.Impl
{
    public class BackupTaskService : IBackupTaskService
    {
        private readonly ILogger<BackupTaskService> _logger;
        private readonly IConfiguration _config;

        public BackupTaskService(ILogger<BackupTaskService> logger
                               , IConfiguration config)
        {
            _logger = logger;
            _config = config;
        }

        public BackupTask GetBackupTask()
        {
            if (IsStandAloneMode())
            {
                return GetBackupTaskFromLocalFile();
            }
            else
            {
                return GetBackupTaskFromCentralServer();
            }
        }

        private bool IsStandAloneMode()
        {
            // TODO: add central server recognition
            return true;
        }

        private BackupTask GetBackupTaskFromCentralServer()
        {
            // TODO: add work with central server
            return null;
        }

        private BackupTask GetBackupTaskFromLocalFile()
        {
            string backupTaskFile = _config.GetValue<string>("BackupTaskFile");

            if (string.IsNullOrWhiteSpace(backupTaskFile))
                throw new ArgumentException("BackupTaskFile config option is null or empty.");

            if (!File.Exists(backupTaskFile))
            {
                _logger.LogInformation("Could not find Backup Task file \"{file}\".", backupTaskFile);
                _logger.LogInformation("Will try to generate a new file.");

                CreateNewBackupTaskFile(backupTaskFile);
            }

            string json = File.ReadAllText(backupTaskFile);
            return JsonConvert.DeserializeObject<BackupTask>(json);
        }

        private void CreateNewBackupTaskFile(string path)
        {
            var localVMs = GetLocalVMsNames();

            if (localVMs.Count == 0)
                throw new Exception("Could not find any virtual machines. Cannot continue.");

            var bt = new BackupTask();
            bt.ParallelBackups = 1;
            bt.VirtualMachines = CreateDefaultVMs(localVMs);

            string json = JsonConvert.SerializeObject(bt, Formatting.Indented);
            File.WriteAllText(path, json);
        }

        private List<VirtualMachine> CreateDefaultVMs(List<string> vmNames)
        {
            var result = new List<VirtualMachine>();

            var disabledSchedule = new ScheduleConfig()
            {
                Type = "SimpleSchedules.DailySchedule",
                OccursOnceAt = "00:00",
                Enabled = false,
                Description = "This schedule is disabled. Check Backup Task properties and after that enable this schedule."
            };

            foreach (string name in vmNames)
            {
                var vm = new VirtualMachine()
                {
                    Name = name,
                    ExportPathTemplate = "C:\\Backup\\{HV_HOST_FULL}\\{HV_GUEST_FULL}\\Daily_{YEAR}_{MONTH}_{DAY}_{HOUR}_{MINUTE}",
                    ExportRotateDays = 2,
                    CreateArchive = true,
                    ArchivePathTemplate = "C:\\Backup\\{HV_HOST_FULL}\\{HV_GUEST_FULL}\\{HV_HOST_SHORT}_HV_VM_{HV_GUEST_FULL}_export_{YEAR}.{MONTH}.{DAY}.{HOUR}.{MINUTE}.zip",
                    ArchiveCompressionLevel = 1,
                    ArchiveRotateDays = 5,
                    SchedulesConfigs = new List<ScheduleConfig>() { disabledSchedule }
                };

                result.Add(vm);
            }

            return result;
        }

        private List<string> GetLocalVMsNames()
        {
            var vmNames = new List<string>();
            string hvNamespace = Get_HyperV_Namespace();

            if (string.IsNullOrWhiteSpace(hvNamespace))
                throw new Exception("Could not detect Hyper-V namespace in WMI API. Check if current host is a valid Hyper-V server.");

            var scope = GetScope(hvNamespace);
            var data = WmiQuery(scope, "SELECT * FROM MSVM_ComputerSystem WHERE Caption != 'Hosting Computer System'");

            if (data != null && data.Count > 0)
            {
                foreach (var item in data)
                {
                    string vmName = item["ElementName"]?.ToString();

                    if (!string.IsNullOrWhiteSpace(vmName))
                    {
                        vmNames.Add(vmName);
                        _logger.LogDebug("Found VM: \"{vm}\"", vmName);
                    }
                }
            }

            return vmNames;
        }

        private ManagementScope GetScope(string wmiNamespace)
        {
            return new ManagementScope($"\\\\localhost\\{wmiNamespace}");
        }

        public static ManagementObjectCollection WmiQuery(ManagementScope scope, string strQuery)
        {
            ManagementObjectCollection result = null;
            ObjectQuery query = new ObjectQuery(strQuery);

            scope.Connect();

            using (var searcher = new ManagementObjectSearcher(scope, query))
            {
                result = searcher.Get();
            }

            return result;
        }

        private string Get_HyperV_Namespace()
        {
            string result = null;

            var scopeRoot = GetScope("ROOT");
            var scopeVirt = GetScope(@"ROOT\virtualization");

            if (NamespaceExists(scopeRoot, "virtualization"))
            {
                result = @"ROOT\virtualization";

                if (NamespaceExists(scopeVirt, "v2"))
                    result = @"ROOT\virtualization\v2";
            }

            _logger.LogDebug("Get_HyperV_Namespace() == \"{ns}\"", result);

            return result;
        }

        private bool NamespaceExists(ManagementScope scope, string wmiNamespace)
        {
            var nsClass = new ManagementClass(scope, new ManagementPath("__NAMESPACE"), null);

            foreach (ManagementObject ns in nsClass.GetInstances())
            {
                if (wmiNamespace.ToLower() == ns["Name"]?.ToString().ToLower())
                    return true;
            }

            return false;
        }
    }
}