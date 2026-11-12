# Answers every request the way a sidecar answers a line it could not parse: an error with
# no id. This is what two interleaved requests from an unlocked writer produce.
import sys

for line in sys.stdin:
    sys.stdout.write('{"jsonrpc":"2.0","error":{"code":-32700,"message":"Parse error: interleaved"}}\n')
    sys.stdout.flush()
