﻿using BackupHyperV.Service.Interfaces;
using BackupHyperV.Service.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.IO;
using System.Management;
using System.Threading;
using System.Xml;

// Code based on these examples:
// https://github.com/microsoft/Windows-classic-samples/tree/master/Samples/Hyper-V
// https://docs.microsoft.com/en-us/windows/win32/hyperv_v2/exporting-virtual-machines

namespace BackupHyperV.Service.Impl
{
    public class VmExporter : IVmExporter
    {
        private readonly ILogger<VmExporter> _logger;

        public VmExporter(ILogger<VmExporter> logger)
        {
            _logger = logger;
        }

        public bool ExportVirtualSystem(VirtualMachine virtualMachine, SnapshotExport snapshotExport)
        {
            virtualMachine.ExportPercentComplete = 0;

            bool exportSuccess = true;
            var scope = WmiRoutines.GetScope(WmiRoutines.NAMESPACE_HYPER_V);

            using (ManagementObject virtualSystemService = WmiRoutines.GetServiceObject(scope, "Msvm_VirtualSystemManagementService"))
            using (ManagementObject srv = GetVirtualMachine(scope, virtualMachine.Name))
            using (ManagementBaseObject inParams = virtualSystemService.GetMethodParameters("ExportSystemDefinition"))
            {
                if (!Directory.Exists(virtualMachine.ExportPath))
                    Directory.CreateDirectory(virtualMachine.ExportPath);

                ManagementPath settingPath = new ManagementPath("Msvm_VirtualSystemExportSettingData");

                using (ManagementClass exportSettingDataClass = new ManagementClass(scope, settingPath, null))
                using (ManagementObject exportSettingData = exportSettingDataClass.CreateInstance())
                {
                    exportSettingData["CopySnapshotConfiguration"] = snapshotExport;
                    exportSettingData["CopyVmRuntimeInformation"] = true;
                    exportSettingData["CopyVmStorage"] = true;
                    exportSettingData["CreateVmExportSubdirectory"] = true;

                    string settingData = exportSettingData.GetText(TextFormat.CimDtd20);

                    inParams["ComputerSystem"] = srv.Path.Path;
                    inParams["ExportDirectory"] = virtualMachine.ExportPath;
                    inParams["ExportSettingData"] = settingData;
                }

                using (ManagementBaseObject outParams = virtualSystemService.InvokeMethod("ExportSystemDefinition",
                                                                inParams, null))
                {
                    exportSuccess = ValidateOutput(virtualMachine, outParams, scope, false, true);
                }
            }

            virtualMachine.ExportPercentComplete = 100;
            return exportSuccess;
        }

        // Gets the Msvm_ComputerSystem instance that matches the requested virtual machine name.
        private ManagementObject GetVirtualMachine(ManagementScope scope, string name)
        {
            string query = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = \"{name}\"";
            var data = WmiRoutines.WmiQuery(scope, query);
            var result = WmiRoutines.GetFirstObjectFromCollection(data);

            if (result == null)
                throw new Exception($"Could not find virtual machine with name \"{name}\"");

            return result;
        }

        // Validates the output parameters of a method call and prints errors, if any.
        private bool ValidateOutput(
                        VirtualMachine virtualMachine,
                        ManagementBaseObject outputParameters,
                        ManagementScope scope,
                        bool throwIfFailed,
                        bool printErrors)
        {
            bool succeeded = true;
            string errorMessage = "The method call failed.";

            if ((uint)outputParameters["ReturnValue"] == ReturnCode.Started)
            {
                //
                // The method invoked an asynchronous operation. Get the Job object
                // and wait for it to complete. Then we can check its result.
                //

                using (ManagementObject job = new ManagementObject((string)outputParameters["Job"]))
                {
                    job.Scope = scope;

                    while (!IsJobComplete(job["JobState"]))
                    {
                        virtualMachine.ExportPercentComplete = Convert.ToInt32(job["PercentComplete"]);

                        Thread.Sleep(500);

                        // 
                        // ManagementObjects are offline objects. Call Get() on the object to have its
                        // current property state.
                        //
                        job.Get();
                    }

                    if (!IsJobSuccessful(job["JobState"]))
                    {
                        succeeded = false;

                        //
                        // In some cases the Job object can contain helpful information about
                        // why the method call failed. If it did contain such information,
                        // use it instead of a generic message.
                        //
                        if (!string.IsNullOrWhiteSpace((string)job["ErrorDescription"]))
                        {
                            errorMessage = (string)job["ErrorDescription"];
                        }

                        if (printErrors)
                        {
                            PrintMsvmErrors(job);
                        }

                        if (throwIfFailed)
                        {
                            throw new ManagementException(errorMessage);
                        }
                    }
                }
            }
            else if ((uint)outputParameters["ReturnValue"] != ReturnCode.Completed)
            {
                succeeded = false;

                if (throwIfFailed)
                {
                    throw new ManagementException(errorMessage);
                }
            }

            return succeeded;
        }

        // Verifies whether a job is completed.
        private bool IsJobComplete(object jobStateObj)
        {
            JobState jobState = (JobState)((ushort)jobStateObj);

            return jobState == JobState.Completed ||
                   jobState == JobState.CompletedWithWarnings ||
                   jobState == JobState.Terminated ||
                   jobState == JobState.Exception ||
                   jobState == JobState.Killed;
        }

        // Verifies whether a job succeeded.
        private bool IsJobSuccessful(object jobStateObj)
        {
            JobState jobState = (JobState)((ushort)jobStateObj);

            return jobState == JobState.Completed || jobState == JobState.CompletedWithWarnings;
        }

        // Prints the relevant message from embedded instances of Msvm_Error.
        private void PrintMsvmErrors(ManagementObject job)
        {
            string[] errorList;

            using (ManagementBaseObject inParams = job.GetMethodParameters("GetErrorEx"))
            using (ManagementBaseObject outParams = job.InvokeMethod("GetErrorEx", inParams, null))
            {
                if ((uint)outParams["ReturnValue"] != ReturnCode.Completed)
                {
                    throw new ManagementException(string.Format(CultureInfo.CurrentCulture,
                                                                "GetErrorEx() call on the job failed"));
                }

                errorList = (string[])outParams["Errors"];
            }

            if (errorList == null)
                return;

            foreach (string error in errorList)
            {
                string errorSource = string.Empty;
                string errorMessage = string.Empty;
                int propId = 0;

                XmlReader reader = XmlReader.Create(new StringReader(error));

                while (reader.Read())
                {
                    if (reader.Name.Equals("PROPERTY", StringComparison.OrdinalIgnoreCase))
                    {
                        propId = 0;

                        if (reader.HasAttributes)
                        {
                            string propName = reader.GetAttribute(0);

                            if (propName.Equals("ErrorSource", StringComparison.OrdinalIgnoreCase))
                            {
                                propId = 1;
                            }
                            else if (propName.Equals("Message", StringComparison.OrdinalIgnoreCase))
                            {
                                propId = 2;
                            }
                        }
                    }
                    else if (reader.Name.Equals("VALUE", StringComparison.OrdinalIgnoreCase))
                    {
                        if (propId == 1)
                        {
                            errorSource = reader.ReadElementContentAsString();
                        }
                        else if (propId == 2)
                        {
                            errorMessage = reader.ReadElementContentAsString();
                        }

                        propId = 0;
                    }
                    else
                    {
                        propId = 0;
                    }
                }

                _logger.LogError("Detailed errors:");
                _logger.LogError("Error Message: {msg}", errorMessage);
                _logger.LogError("Error Source:  {src}", errorSource);
            }
        }
    }
}
