# CheckForAppCrash

This project requires the path to a trace containing process information to be passed in.
When run it exits with a status code of 1 if wefault.exe is detected running during the trace, indicating that a crash
occurred during the trace.
It exits with a status code of 0 otherwise.