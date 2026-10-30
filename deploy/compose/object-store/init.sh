#!/bin/sh
# The object store's one-shot (§14.1): init.txt's commands against the master named in $1. SeaweedFS's shell exits
# 1 on a bucket that exists since 4.50, so once the store lists init.txt's bucket its create line is left out; the
# policies and the expiry are rewritten as they stand, which is what lets a second `up` succeed. The name loses any
# carriage return, as a Windows checkout writes init.txt with CRLF.
set -eu
master="$1"
commands=/etc/seaweedfs/init.txt
bucket=$(sed -n 's/^s3\.bucket\.create -name //p' "$commands" | tr -d '\r')
tab=$(printf '\t')

if echo s3.bucket.list | weed shell -master="$master" | grep -q "^  $bucket$tab"; then
    grep -v '^s3\.bucket\.create ' "$commands" | weed shell -master="$master"
else
    weed shell -master="$master" < "$commands"
fi
