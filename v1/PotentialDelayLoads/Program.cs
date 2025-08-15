// © Microsoft Corporation. All rights reserved.

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Memory;
using Microsoft.Windows.EventTracing.Processes;
using Microsoft.Windows.EventTracing.Symbols;
using System;
using System.Collections.Generic;

namespace StackHitTime
{
    static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("Usage: PotentialDelayLoads.exe <trace.etl>");
                return 1;
            }

            string tracePath = args[0];

            var settings = new TraceProcessorSettings
            {
                AllowLostEvents = true,
            };

            using (ITraceProcessor trace = TraceProcessor.Create(tracePath, settings))
            {
                IPendingResult<IReferenceSetDataSource> pendingReferenceSet = trace.UseReferenceSetData();
                IPendingResult<IProcessDataSource> pendingProcesses = trace.UseProcesses();
                IPendingResult<ISymbolDataSource> pendingSymbols = trace.UseSymbols();
                IPendingResult<IImageSectionDataSource> pendingImageSections = trace.UseImageSections();

                trace.Process();

                IProcessDataSource processData = pendingProcesses.Result;
                IReferenceSetDataSource referenceSetData = pendingReferenceSet.Result;
                ISymbolDataSource symbolData = pendingSymbols.Result;
                IImageSectionDataSource imageSectionData = pendingImageSections.Result;

                symbolData.LoadSymbolsForConsoleAsync(SymCachePath.Automatic, SymbolPath.Automatic).GetAwaiter().GetResult();

                //
                // Create a mapping of all static images loaded into all processes during the course of the trace.
                // This is a mapping of images to a dictionary of [processes, IsPotentialDelayLoadTarget]
                //
                Dictionary<string, Dictionary<string, bool>> potentialDelayLoads = new Dictionary<string, Dictionary<string, bool>>();

                //
                // Keep track of the image data for all of the images we've seen loaded. We use this later to look up
                // section names for the offsets being accessed.
                //
                Dictionary<string, IImage> imageData = new Dictionary<string, IImage>();

                foreach (var proc in processData.Processes)
                {
                    foreach (var image in proc.Images)
                    {
                        string processName = GenerateProcessNameString(proc);
                        if (image.LoadTime != null)
                        {
                            Dictionary<string, bool> processDict;
                            if (!potentialDelayLoads.ContainsKey(image.Path))
                            {
                                processDict = new Dictionary<string, bool>();
                                potentialDelayLoads.Add(image.Path, processDict);
                            }
                            else
                            {
                                processDict = potentialDelayLoads[image.Path];
                            }

                            if (!processDict.ContainsKey(processName))
                            {
                                bool eligibleForDelayLoad = (image.LoadReason == ImageLoadReason.StaticDependency);
                                processDict.Add(processName, eligibleForDelayLoad);
                            }

                            //
                            // Save off whether or not this image is a potential delay load target. We only consider
                            // static dependencies for delay loads.
                            //
                            processDict[processName] = processDict[processName] && (image.LoadReason == ImageLoadReason.StaticDependency);
                            
                            //
                            // Save off a pointer to the image data for this image so we can look up sections later
                            //
                            if (!imageData.ContainsKey(image.Path))
                                imageData.Add(image.Path, image);
                        }
                    }
                }

                //
                // Enumerate every page access. We're going to check each one to see if it was a 'code' page being accessed,
                // and if it was we conclude that code from this image was used during the trace by that process. Therefore,
                // it's not something that should be delay loaded.
                //
                foreach (IReferenceSetInterval refSetInterval in referenceSetData.Intervals)
                {
                    foreach (IReferenceSetAccessedPage pageAccess in refSetInterval.PageAccesses)
                    {
                        //
                        // Make sure the page was accessed from the usermode process.
                        //
                        if (pageAccess.ImpactedProcess == null)
                            continue;

                        //
                        // Ignore the memory compression process. This is a system service.
                        //
                        if (pageAccess.ImpactedProcess.ImageName.Equals("MemCompression"))
                            continue;

                        //
                        // Make sure we have a file path
                        //
                        if (pageAccess?.Page?.Path == null)
                            continue;
                        var fileBeingAccessed = pageAccess?.Page?.Path;

                        //
                        // Not all file paths are images (think MFT or data files). Make sure this is in our image
                        // dictionary. 
                        //
                        if (!imageData.ContainsKey(pageAccess.Page.Path))
                            continue;

                        //
                        // Make sure that this image was listed in the image data 
                        //
                        if (!potentialDelayLoads.ContainsKey(fileBeingAccessed))
                            continue;

                        //
                        // Grab the image data, and use this to get the info on the page that was being accessed.
                        //
                        var data = imageData[pageAccess.Page.Path];
                        var sectionName = GetSectionNameFromPage(pageAccess, data, imageSectionData, pageAccess.ImpactedProcess);

                        //
                        // We really only want consider .text pages, as we want to find images where the 'code' is never
                        // used. We have to include "unknown" as well since this is what shows up for images that we
                        // can't find symbols for. This effectively means for images without symbols we consider all pages.
                        //
                        if (!(sectionName.Contains(".text") || sectionName.Contains("Unknown")))
                            continue;

                        //
                        // If the loader accessed the page, it's still a potential delay load candidiate.
                        //
                        if (IsLoaderAccessedPage(pageAccess))
                            continue;

                        //
                        // A .text page was accessed from somewhere other then the loader. This image isn't
                        // a delay load candidate for this process.
                        //
                        string processName = GenerateProcessNameString(pageAccess.ImpactedProcess);
                        if ((potentialDelayLoads[fileBeingAccessed]).ContainsKey(processName))
                        {
                            if ((potentialDelayLoads[fileBeingAccessed])[processName])
                            {
                                (potentialDelayLoads[fileBeingAccessed])[processName] = false;
                            }
                        }
                        else
                        {
                            potentialDelayLoads[fileBeingAccessed].Add(processName, false);
                        }
                    }
                }

                //
                // Print out all potential delays loads we found. We modify the output format to be in 
                // process->image style for easier consumption from the console.
                //
                List<Tuple<string, string>> delayLoads = new List<Tuple<string, string>>();
                foreach (var imagesLoaded in potentialDelayLoads)
                {
                    foreach (var processesDict in imagesLoaded.Value)
                    {
                        if (processesDict.Value == true)
                        {
                            delayLoads.Add(new Tuple<string, string>(processesDict.Key, imagesLoaded.Key));
                        }
                    }
                }
                delayLoads.Sort();
                foreach (var delayload in delayLoads)
                {
                    Console.WriteLine("{0} can delay load {1}", delayload.Item1, delayload.Item2);
                }
            }

            return 0;
        }

        static bool IsLoaderAccessedPage(IReferenceSetAccessedPage page)
        {
            if (page.Page.Category == ResidentSetPageCategory.Image || page.Page.Category == ResidentSetPageCategory.CopyOnWriteImage)
            {
                if (page.AccessingStack != null)
                {
                    foreach (var frame in page.AccessingStack.Frames)
                    {
                        var frameImage = frame?.Image?.FileName;
                        var frameFunction = frame?.Symbol?.FunctionName;

                        if (frameImage != null &&
                            frameImage.Contains("ntdll") &&
                            frameFunction != null &&
                            (frameFunction.Contains("LdrpPrepareModuleForExecution") ||
                            frameFunction.Contains("LdrUnloadDll") ||
                            frameFunction.Contains("LdrpDrainWorkQueue")))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        public static string GetSectionNameFromPage(IReferenceSetAccessedPage accessedPage, IImage passedInImageData, IImageSectionDataSource imageSections, IProcess processContext)
        {
            string sectionName = "Unknown";

            if (accessedPage.Page.Category == ResidentSetPageCategory.Image ||
                accessedPage.Page.Category == ResidentSetPageCategory.CopyOnWriteImage ||
                 accessedPage.Page.Category == ResidentSetPageCategory.SessionCopyOnWriteImage ||
                 accessedPage.Page.Category == ResidentSetPageCategory.Driver)
            {
                //
                // Look up the section name based on the file offset being accessed. 
                //
                if (accessedPage?.Page != null)
                {
                    ulong offset = accessedPage?.Page?.FileOffset ?? 0;

                    if (offset == 0)
                    {
                        sectionName = "ImageHeader";
                    }
                    else
                    {
                        if (passedInImageData.Pdb != null && passedInImageData.Pdb.IsLoaded)
                        {
                            var sections = passedInImageData.GetImageSections(imageSections);

                            foreach (var s in sections)
                            {
                                var sectionRange = s.FileAddressRange;
                                if (offset >= sectionRange.BaseAddress.Value &&
                                    offset < sectionRange.LimitAddress.Value)
                                {
                                    return s.Name;
                                }
                            }
                        }
                    }
                }
            }

            return sectionName;
        }

        static string GenerateProcessNameString(IProcess process)
        {
            return String.Format("{0} ({1})",process.ImageName, process.Id);
        }
    }
}
