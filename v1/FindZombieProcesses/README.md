# FindZombieProcesses

This project requires the path to a trace containing handle data to be passed in.
When run it prints any processes that were being kept alive in a zombie state due to unclosed handles at the end of
the trace, and which processes were holding those handles.