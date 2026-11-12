namespace Pole.Features.Postgres.Application.Backups

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Amazon.Runtime
open Amazon.S3
open Amazon.S3.Model
open Pole.Features.Backups.Contracts

[<RequireQualifiedAccess>]
module BarmanCatalog =
    let private validBackupId id =
        DateTime.TryParseExact(id, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None) |> fst

    let parseEntry (backupId: string) (metadata: string) =
        if not (validBackupId backupId) then
            raise (InvalidDataException("InvalidBackupCatalogId"))

        let fields = Dictionary<string, string>(StringComparer.Ordinal)
        use reader = new StringReader(metadata)
        let mutable line = reader.ReadLine()

        while not (isNull line) do
            let separator = line.IndexOf('=')
            if separator > 0 then
                let key = line.Substring(0, separator)
                if key = "status" || key = "end_time" then
                    if not (fields.TryAdd(key, line.Substring(separator + 1).Trim())) then
                        raise (InvalidDataException("DuplicateBackupInfoField"))
            line <- reader.ReadLine()

        match fields.TryGetValue("status") with
        | true, "DONE" ->
            match fields.TryGetValue("end_time") with
            | true, timestamp ->
                let mutable ended = DateTimeOffset.MinValue
                let offset = System.Text.RegularExpressions.Regex.IsMatch(timestamp, @"(?:Z|[+-]\d{2}:?\d{2})$")
                if not offset || not (DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, &ended)) then
                    raise (InvalidDataException("InvalidBackupEndTime"))
                Some(BackupCatalogEntry(backupId, ended.ToUniversalTime()))
            | _ -> raise (InvalidDataException("MissingBackupEndTime"))
        | true, ("EMPTY" | "STARTED" | "WAITING_FOR_WALS" | "SYNCING" | "FAILED") -> None
        | _ -> raise (InvalidDataException("InvalidBackupStatus"))

    let private readMetadata (client: #IAmazonS3) bucket key (ct: CancellationToken) =
        task {
            use! response = client.GetObjectAsync(GetObjectRequest(BucketName = bucket, Key = key), ct)
            let limit = 1024 * 1024
            use bytes = new MemoryStream()
            let buffer = Array.zeroCreate<byte> 8192
            let mutable more = true
            while more do
                let! count = response.ResponseStream.ReadAsync(buffer.AsMemory(), ct)
                if count = 0 then more <- false
                elif bytes.Length + int64 count > int64 limit then raise (InvalidDataException("BackupInfoTooLarge"))
                else bytes.Write(buffer, 0, count)
            return UTF8Encoding(false, true).GetString(bytes.ToArray())
        }

    let readWith (client: #IAmazonS3) (archive: BackupArchiveContext) ct =
        task {
            let prefix = String.Join('/', [| archive.Prefix.Trim('/'); archive.ServerName; "base" |] |> Array.filter (String.IsNullOrEmpty >> not)) + "/"
            let query = ListObjectsV2Request(BucketName = archive.Bucket, Prefix = prefix, Delimiter = "/")
            let entries = ResizeArray<BackupCatalogEntry>()
            let mutable more = true
            while more do
                let! page = client.ListObjectsV2Async(query, ct)
                if not (isNull page.CommonPrefixes) then
                    for folder in page.CommonPrefixes do
                        if not (folder.StartsWith(prefix, StringComparison.Ordinal) && folder.EndsWith("/", StringComparison.Ordinal)) then
                            raise (InvalidDataException("InvalidBackupPrefix"))
                        let id = folder.Substring(prefix.Length).TrimEnd('/')
                        if not (validBackupId id) then raise (InvalidDataException("InvalidBackupCatalogId"))
                        let! metadata = readMetadata client archive.Bucket (folder + "backup.info") ct
                        parseEntry id metadata |> Option.iter entries.Add
                more <- page.IsTruncated.GetValueOrDefault()
                if more && (String.IsNullOrEmpty(page.NextContinuationToken) || page.NextContinuationToken = query.ContinuationToken) then
                    raise (InvalidDataException("InvalidBackupCatalogPage"))
                query.ContinuationToken <- page.NextContinuationToken
            return entries |> Seq.sortByDescending _.CompletedAt |> Seq.toArray :> IReadOnlyList<_>
        }

    let private signingRegionHint (ex: AmazonS3Exception) =
        if ex.ErrorCode <> "AuthorizationHeaderMalformed" || String.IsNullOrEmpty ex.ResponseBody || ex.ResponseBody.Length > 8192 then None
        else
            try
                let settings = System.Xml.XmlReaderSettings(DtdProcessing = System.Xml.DtdProcessing.Prohibit, MaxCharactersInDocument = 8192L)
                use input = new StringReader(ex.ResponseBody)
                use reader = System.Xml.XmlReader.Create(input, settings)
                let response = System.Xml.Linq.XDocument.Load(reader)
                response.Root.Elements()
                |> Seq.tryFind (fun e -> e.Name.LocalName = "Region")
                |> Option.map _.Value
                |> Option.filter (fun value -> System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z0-9-]{1,64}$"))
            with :? System.Xml.XmlException -> None

    let read (request: ListBackupCatalogRequest) (ct: CancellationToken) =
        task {
            use deadline = CancellationTokenSource.CreateLinkedTokenSource(ct)
            deadline.CancelAfter(TimeSpan.FromSeconds(30.))
            let endpoint = request.Archive.Endpoint
            let region = Amazon.Util.AWSSDKUtils.DetermineRegion(endpoint)
            let readRegion signingRegion =
                task {
                    let config =
                        AmazonS3Config(ServiceURL = endpoint, AuthenticationRegion = signingRegion,
                                       ForcePathStyle = true, AllowAutoRedirect = false, MaxErrorRetry = 1)
                    use client = new AmazonS3Client(BasicAWSCredentials(request.Credentials.AccessKeyId, request.Credentials.SecretAccessKey), config)
                    return! readWith client request.Archive deadline.Token
                }
            try
                let! items =
                    task {
                        try
                            return! readRegion region
                        with :? AmazonS3Exception as ex ->
                            match signingRegionHint ex with
                            | Some hint when hint <> region ->
                                // Re-sign for the advertised bucket region against the same authorized endpoint.
                                return! readRegion hint
                            | _ -> return raise ex
                    }
                return BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>.Success(items) :> BackupProviderResult<_>
            with
            | :? OperationCanceledException when not ct.IsCancellationRequested ->
                return BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>.Failure("backup-catalog-timeout", true) :> _
            | :? AmazonS3Exception as ex ->
                let denied = ex.StatusCode = HttpStatusCode.Forbidden || ex.StatusCode = HttpStatusCode.Unauthorized
                return BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>.Failure((if denied then "backup-catalog-access-denied" else "backup-catalog-unavailable"), not denied) :> _
            | :? InvalidDataException | :? DecoderFallbackException ->
                return BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>.Failure("backup-catalog-unreadable", false) :> _
            | :? HttpRequestException | :? IOException ->
                return BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>.Failure("backup-catalog-unavailable", true) :> _
        }
