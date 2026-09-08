#!/bin/sh
# ASB_FCNTL_TRACE=1 preloads a small shim that writes every failed fcntl() lock call, and every lock call on a
# catalog.db file, to stderr — i.e. to the container log. It exists because the one production failure that
# could not be explained from outside the process was a byte-range lock the kernel refused for no visible reason
# (see ProcessPrivateSqlite); with the trace on, the next such refusal arrives with its errno.
set -e
if [ "${ASB_FCNTL_TRACE:-0}" = "1" ]; then
    export LD_PRELOAD=/app/fcntlspy.so
fi
exec dotnet AzureStorageBackup.Api.dll "$@"
