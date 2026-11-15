#!/usr/bin/env python3
"""Generate the exact operator-0.1.47 acceptance fixture; emit no credentials."""
import argparse
import json

GROUP = "seaweed.seaweedfs.com/v1"


def resource(kind, name, spec):
    return {"apiVersion": GROUP, "kind": kind, "metadata": {"name": name}, "spec": spec}


def anti_affinity(component):
    return {"podAntiAffinity": {"requiredDuringSchedulingIgnoredDuringExecution": [{
        "labelSelector": {"matchLabels": {"app.kubernetes.io/instance": "store", "app.kubernetes.io/component": component}},
        "topologyKey": "kubernetes.io/hostname"}]}}


def manifests(stage, image, nodes, data_node=""):
    ref = {"name": "store"}
    if stage == "metadata":
        return [{"apiVersion": "postgresql.cnpg.io/v1", "kind": "Cluster", "metadata": {"name": "store-metadata"},
            "spec": {"instances": 3, "imageName": "ghcr.io/cloudnative-pg/postgresql:17.6-standard-trixie",
            "enableSuperuserAccess": False,
            "bootstrap": {"initdb": {"database": "seaweedfs", "owner": "seaweedfs", "secret": {"name": "metadata-credentials"}, "localeCollate": "C", "localeCType": "C"}},
            "postgresql": {"synchronous": {"method": "any", "number": 1, "dataDurability": "required", "failoverQuorum": True}},
            "affinity": {"enablePodAntiAffinity": True, "podAntiAffinityType": "required", "topologyKey": "kubernetes.io/hostname"},
            "storage": {"size": "1Gi", "storageClass": "local-path"},
            "resources": {"requests": {"cpu": "100m", "memory": "256Mi"}, "limits": {"memory": "512Mi"}}}}]
    if stage == "cluster":
        def component(name):
            result = {"readinessProbe": {"initialDelaySeconds": 2, "periodSeconds": 2}, "replicas": nodes, "requests": {"cpu": "50m", "memory": "64Mi"}}
            if nodes == 3:
                result["affinity"] = anti_affinity(name)
            return result
        master = component("master")
        master.update({"config": "[master.volume_growth]\ncopy_1 = 1\ncopy_2 = 1\ncopy_3 = 1\ncopy_other = 1\n", "defaultReplication": "001" if nodes == 3 else "000", "volumeSizeLimitMB": 64,
                       "persistence": {"enabled": True, "resources": {"requests": {"storage": "1Gi"}}, "storageClassName": "local-path"}})
        volume = component("volume")
        volume["requests"]["storage"] = "1Gi"
        volume.update({"maxVolumeCounts": 15, "minFreeSpacePercent": 1, "storageClassName": "local-path"})
        filer = component("filer")
        if nodes == 3:
            filer["configSecret"] = {"name": "filer-store", "key": "filer.toml"}
        else:
            filer.update({"config": '[leveldb2]\nenabled = true\ndir = "/data/filerldb2"\n',
                          "persistence": {"enabled": True, "resources": {"requests": {"storage": "1Gi"}}, "storageClassName": "local-path"}})
        gateway = component("s3")
        gateway.update({"configSecret": {"name": "bootstrap", "key": "s3.json"},
                        "labels": {"pole.io/resource-deployment-id": "seaweedfs-acceptance"}})
        result = [resource("Seaweed", "store", {"image": image, "volumeServerDiskCount": 1,
                    "master": master, "volume": volume, "filer": filer, "s3": gateway})]
        if nodes == 3:
            result.append(resource("AdminScript", "repair", {"clusterRef": ref, "schedule": "*/1 * * * *", "script": "lock\nvolume.fix.replication -apply\nunlock\n", "activeDeadlineSeconds": 300, "concurrencyPolicy": "Forbid", "backoffLimit": 0, "successfulJobsHistoryLimit": 1, "failedJobsHistoryLimit": 1}))
        return result
    if stage == "iam":
        result = [resource("Bucket", name, {"seaweedRef": ref, "reclaimPolicy": "Retain",
                         "placement": {"replication": "001" if nodes == 3 else "000", "fsync": True}})
                  for name in ("retired-bucket", "sibling-bucket")]
        for bucket in result:
            bucket["spec"]["placement"]["volumeGrowthCount"] = 2 if nodes == 3 else 1
            if data_node and bucket["metadata"]["name"] == "retired-bucket":
                bucket["spec"]["placement"]["dataNode"] = data_node
        for identity in ("owner", "extra"):
            buckets = ["retired-bucket"] + (["sibling-bucket"] if identity == "extra" else [])
            result.extend([resource("S3Identity", identity, {"seaweedRef": ref}),
                resource("S3Credentials", identity, {"seaweedRef": ref, "identityRef": {"name": identity}, "secretRef": {"name": identity}}),
                resource("S3Policy", identity, {"seaweedRef": ref, "statements": [{"effect": "Allow",
                    "actions": ["s3:ListBucket", "s3:GetObject", "s3:PutObject", "s3:DeleteObject"],
                    "resources": [path for bucket in buckets for path in (bucket, bucket + "/*")]}]}),
                resource("S3PolicyBinding", identity, {"seaweedRef": ref, "policyRef": {"name": identity},
                    "subjects": [{"kind": "S3Identity", "name": identity}]})])
        return result
    if stage == "rotate":
        return [resource("S3Credentials", "owner-new", {"seaweedRef": ref,
            "identityRef": {"name": "owner"}, "secretRef": {"name": "owner-new"}})]
    if stage == "retire":
        policy = {"Version": "2012-10-17", "Statement": [{"Effect": "Deny", "Principal": "*", "Action": "s3:*",
            "Resource": ["arn:aws:s3:::retired-bucket", "arn:aws:s3:::retired-bucket/*"]}]}
        return [resource("S3Policy", "retire", {"seaweedRef": ref, "policyDocument": json.dumps(policy)}),
            resource("S3PolicyBinding", "retire", {"seaweedRef": ref, "policyRef": {"name": "retire"},
                "subjects": [{"kind": "Bucket", "name": "retired-bucket"}]})]
    raise ValueError(stage)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stage", choices=["cluster", "iam", "rotate", "retire", "metadata"])
    parser.add_argument("--image", default="docker.io/chrislusf/seaweedfs:4.53")
    parser.add_argument("--nodes", type=int, choices=[1, 3], default=1)
    parser.add_argument("--data-node", default="")
    args = parser.parse_args()
    print(json.dumps({"apiVersion": "v1", "kind": "List", "items": manifests(args.stage, args.image, args.nodes, args.data_node)}))
