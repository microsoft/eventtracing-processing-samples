# PotentialDelayLoads

This project requires the path to a trace containing processes and reference set data to be passed in.
When run it prints information about any DLLs that were loaded by a process as a static dependency, but that only had
initialization code called.
In other words, it outputs any DLLs that could be delay loaded for potential performance gains within the traced
scenario.
For more information on delay loading see
https://docs.microsoft.com/en-us/cpp/build/reference/linker-support-for-delay-loaded-dlls.