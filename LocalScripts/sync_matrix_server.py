#!/usr/bin/env python3
"""Private fault server for run_sync_matrix.py; never production/backend evidence."""
import argparse
import json
from pathlib import Path
import socket
import threading
import time
from http.server import ThreadingHTTPServer
from urllib.parse import urlparse
import local_geojson_server as base


class Handler(base.Handler):
    fault_path = None
    fault_lock = threading.Lock()

    def claim(self, mode):
        if self.headers.get('X-AR2-Test') == 'harness' or not self.headers.get('User-Agent','').startswith('UnityPlayer'):
            return False
        path=urlparse(self.path).path
        if not path.startswith(base.API_BASE) or self.command not in ('PUT','PATCH','DELETE'):return False
        with self.fault_lock:
            try:f=json.loads(Path(self.fault_path).read_text())
            except (FileNotFoundError,json.JSONDecodeError):return False
            if f.get('mode')!=mode or not f.get('remaining',0):return False
            f['remaining']-=1
            Path(self.fault_path).write_text(json.dumps(f))
            with open(str(self.fault_path)+'.jsonl','a') as log:
                log.write(json.dumps({'time_ns':time.time_ns(),'mode':mode,'method':self.command,'path':path})+'\n')
            return True

    def gate(self):
        if not self.claim('server_error'):return False
        self._read_json_body()
        super()._json(503,{'error':'Deliberate one-shot matrix failure before mutation'})
        return True

    def do_PUT(self):
        if not self.gate():super().do_PUT()

    def do_PATCH(self):
        if not self.gate():super().do_PATCH()

    def do_DELETE(self):
        if not self.gate():super().do_DELETE()

    def _json(self,status,payload):
        if 200<=status<300 and self.claim('lost_response'):
            # Production store mutation has already completed. Audit truthfully
            # records that the successful response was never delivered.
            record={'time_ns':time.time_ns(),'method':self.command,'path':urlparse(self.path).path,
                    'status':0,'applied_status':status,'outcome':'response_dropped_after_commit',
                    'actor':'client','user_agent':self.headers.get('User-Agent',''),'body':getattr(self,'_audit_body',None)}
            with self.audit_lock:
                with open(self.audit_log,'a') as log:log.write(json.dumps(record)+'\n')
            self.close_connection=True
            try:self.connection.shutdown(socket.SHUT_RDWR)
            except OSError:pass
            self.connection.close()
            return
        super()._json(status,payload)


def main():
    p=argparse.ArgumentParser();p.add_argument('--db',required=True);p.add_argument('--folder',required=True);p.add_argument('--port',type=int,default=8080)
    a=p.parse_args();Handler.store=base.GeoJsonStore(a.db,rewrite_create_ids=True)
    Handler.audit_log=str(Path(a.folder)/'requests.jsonl');Handler.fault_path=str(Path(a.folder)/'fault.json')
    ThreadingHTTPServer(('127.0.0.1',a.port),Handler).serve_forever()

if __name__=='__main__':main()
