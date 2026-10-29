"""Tests for the observer/runner; these do not substitute for running Unity."""
import copy
import importlib.util
import json
import socket
import tempfile
import unittest
from pathlib import Path
from urllib.request import Request, urlopen

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("ar2_offline_sync", REPO / "LocalScripts" / "test_ar2_offline_sync.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


def point(fid):
    return {"type": "Feature", "id": fid, "properties": {"id": fid, "class": "animal", "source": "test"},
            "geometry": {"type": "Point", "coordinates": [23.7, 37.9, 80]}}


def offline():
    return {"type": "FeatureCollection", "features": [point("new")], "_ar2Sync": {
        "schemaVersion": 1, "remoteIds": {"old": "server_old"}, "pending": [
            {"kind": "delete", "featureId": "old", "status": "pending"},
            {"kind": "create", "featureId": "new", "status": "pending", "mayHaveReachedServer": False}]}}


class ObserverTests(unittest.TestCase):
    def test_server_assigned_identity_survives_updates_and_is_used_for_deletion(self):
        with tempfile.TemporaryDirectory() as d:
            directory=Path(d);db=directory/'db.json'
            runner.write_json(db,{'type':'FeatureCollection','features':[]})
            with socket.socket() as probe:probe.bind(('127.0.0.1',0));port=probe.getsockname()[1]
            server=runner.LocalServer(db,directory,port,rewrite_ids=True)
            try:
                server.start();incoming=point('feature_local');incoming['properties']['ar_client_id']='feature_local'
                created=server.request(runner.FEATURES,'PUT',incoming);sid=created['id']
                self.assertNotEqual(sid,'feature_local');self.assertEqual(created['feature']['id'],sid)
                self.assertEqual(created['feature']['properties']['id'],sid)
                self.assertEqual(created['feature']['properties']['ar_client_id'],'feature_local')
                changed=copy.deepcopy(incoming);changed['geometry']['coordinates'][2]+=1
                result=server.request(runner.FEATURES+'/'+sid,'PATCH',changed)
                self.assertEqual(result['id'],sid);self.assertEqual(result['feature']['properties']['id'],sid)
                fetched=server.request(runner.FEATURES)['features']
                self.assertEqual(len(fetched),1);self.assertEqual(fetched[0]['id'],sid)
                self.assertEqual(fetched[0]['geometry'],changed['geometry'])
                self.assertEqual(fetched[0]['properties']['ar_client_id'],'feature_local')
                server.stop();server.start()
                self.assertEqual(server.request(runner.FEATURES)['features'][0]['id'],sid)
                server.request(runner.FEATURES+'/'+sid,'DELETE')
                self.assertEqual(server.request(runner.FEATURES)['features'],[])
            finally:server.stop()

    def test_create_recovery_reads_new_and_legacy_identity_names(self):
        scenario=runner.scenario_from_snapshot(offline())
        for name in ('ar_client_id','ar2_client_id'):
            local=offline();local['_ar2Sync']['pending']=[]
            local['_ar2Sync']['remoteIds']={'new':'server_new'}
            remote=point('server_new');remote['properties'][name]='new'
            self.assertEqual(runner.convergence_problems(scenario,local,{'features':[remote]}),[])
        remote['properties']['ar_client_id']='canonical'
        self.assertEqual(runner.client_identity(remote),'canonical')

    def test_offline_scenario_is_read_without_mutation(self):
        root = offline()
        before = copy.deepcopy(root)
        self.assertEqual(runner.scenario_from_snapshot(root), {"creates": ["new"], "deletes": {"old": "server_old"}})
        self.assertEqual(root, before)

    def test_ambiguous_create_is_not_claimed_as_an_ordinary_offline_create(self):
        root = offline()
        root["_ar2Sync"]["pending"][1]["mayHaveReachedServer"] = True
        with self.assertRaises(runner.TestFailure):
            runner.scenario_from_snapshot(root)

    def test_convergence_requires_acknowledgements_identity_and_matching_content(self):
        scenario = runner.scenario_from_snapshot(offline())
        local = offline()
        local["_ar2Sync"]["pending"] = []
        local["_ar2Sync"]["remoteIds"] = {"new": "server_new"}
        remote_point = point("server_new")
        remote_point["properties"]["ar_client_id"] = "new"
        remote = {"features": [remote_point]}
        self.assertEqual(runner.convergence_problems(scenario, local, remote), [])
        remote["features"].append(copy.deepcopy(remote_point))
        self.assertTrue(runner.convergence_problems(scenario, local, remote))
        remote["features"].pop()
        remote_point["geometry"]["coordinates"][0] += 1
        self.assertTrue(runner.convergence_problems(scenario, local, remote))

    def test_stale_deleted_feature_is_detected(self):
        scenario = runner.scenario_from_snapshot(offline())
        self.assertTrue(runner.convergence_problems(scenario, offline(), {"features": [point("server_old")]}))

    def test_observer_checks_default_payloads_and_rejects_spam(self):
        base = {"time_ns": 10, "actor": "client", "user_agent": "UnityPlayer/test", "status": 200}
        write = dict(base, method="PUT", path=runner.FEATURES, body=point("new"))
        publish = dict(base, method="PATCH", path=runner.STATUS, body=runner.AR_FLAGS)
        ack = dict(base, method="PATCH", path=runner.STATUS, body={"ar_updated": 0})
        self.assertEqual(runner.observer_problems([write, publish, ack], 0), [])
        self.assertTrue(runner.observer_problems([write, publish, publish], 0))
        self.assertTrue(runner.observer_problems([write, dict(publish, body={"ar_updated": 1})], 0))

    def test_harness_and_frontend_requests_are_not_counted_as_ar_writes(self):
        record = {"time_ns": 10, "actor": "harness", "user_agent": "UnityPlayer/test", "status": 200,
                  "method": "PUT", "path": runner.FEATURES, "body": point("new")}
        self.assertTrue(runner.observer_problems([record], 0))

    def test_existing_listener_is_not_terminated(self):
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            listener.listen()
            server = runner.LocalServer("unused", ".", listener.getsockname()[1])
            with self.assertRaises(runner.TestFailure):
                server.require_free_port()
            self.assertGreater(listener.fileno(), -1)

    def test_real_server_restart_persists_data_and_audits_requests(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            db = directory / "db.json"
            runner.write_json(db, {"type": "FeatureCollection", "features": [point("old")],
                "observer_status": {"fe_updated": 1, "mobile_updated": 1, "ar_updated": 1}})
            with socket.socket() as probe:
                probe.bind(("127.0.0.1", 0))
                port = probe.getsockname()[1]
            server = runner.LocalServer(db, directory, port, rewrite_ids=True)
            try:
                original = db.read_bytes()
                db.write_bytes(b"\xef\xbb\xbf" + original)
                with self.assertRaises(json.JSONDecodeError):
                    server.start()
                self.assertEqual(db.read_bytes(), b"\xef\xbb\xbf" + original)
                db.write_bytes(original)
                server.start()
                server.request(runner.FEATURES + "/old", "DELETE")
                created = server.request(runner.FEATURES, "PUT", point("new"))
                self.assertTrue(created["id"].startswith("server_"))
                server.request(runner.STATUS, "PATCH", {"ar_updated": 0})
                status = server.request(runner.STATUS)
                self.assertEqual((status["fe_updated"], status["mobile_updated"], status["ar_updated"]), (1, 1, 0))
                server.stop()
                self.assertEqual([f["id"] for f in runner.read_json(db)["features"]], [created["id"]])
                server.start()
                self.assertEqual([f["id"] for f in server.request(runner.FEATURES)["features"]], [created["id"]])
                # An actual HTTP request verifies the optional request audit.
                request = Request(f"http://127.0.0.1:{port}{runner.STATUS}", method="PATCH",
                    data=json.dumps(runner.AR_FLAGS).encode(), headers={"Content-Type": "application/json", "User-Agent": "UnityPlayer/test"})
                with urlopen(request, timeout=2) as response:
                    self.assertEqual(response.status, 200)
                records = runner.audit_records(server.audit)
                self.assertTrue(any(r.get("body") == runner.AR_FLAGS and r.get("user_agent") == "UnityPlayer/test" for r in records))
            finally:
                server.stop()


if __name__ == "__main__":
    unittest.main()

