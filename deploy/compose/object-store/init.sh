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

# The store reports healthy on its S3 listener before its master has a raft leader, and a list asked of it then
# prints an error and no bucket, which read as absent would send a kept bucket to the create line. So the list is
# asked again until it answers, up to sixty times a second apart.
attempts=60
until listing=$(echo s3.bucket.list | weed shell -master="$master" 2>&1) &&
    ! printf '%s\n' "$listing" | grep -qi '^error'; do
    attempts=$((attempts - 1))
    if [ "$attempts" -le 0 ]; then
        printf '%s\n' "$listing" >&2
        echo "The master at $master did not list the buckets in sixty tries." >&2
        exit 1
    fi
    sleep 1
done

if printf '%s\n' "$listing" | grep -q "^  $bucket$tab"; then
    grep -v '^s3\.bucket\.create ' "$commands" | weed shell -master="$master"
else
    weed shell -master="$master" < "$commands"
fi
