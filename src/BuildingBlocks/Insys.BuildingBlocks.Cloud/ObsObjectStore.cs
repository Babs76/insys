/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Cloud
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Insys.BuildingBlocks.Application.Storage;
using Microsoft.Extensions.Options;

namespace Insys.BuildingBlocks.Cloud;

/// <summary>
/// Huawei Cloud OBS settings (Huawei:Obs; ADR-016). OBS is reached through its S3-compatible API, so the same adapter
/// works against any S3-compatible store (MinIO in the contract tests). Credentials come from the secret store.
/// </summary>
public sealed class ObsOptions
{
    /// <summary>The regional endpoint, https://obs.af-south-1.myhuaweicloud.com in production.</summary>
    public string ServiceUrl { get; set; } = "https://obs.af-south-1.myhuaweicloud.com";

    public string Region { get; set; } = "af-south-1";

    public string Bucket { get; set; } = "";

    public string AccessKey { get; set; } = "";

    public string SecretKey { get; set; } = "";

    /// <summary>Path-style addressing (bucket in the path): needed by MinIO; OBS uses virtual-hosted style.</summary>
    public bool ForcePathStyle { get; set; }
}

/// <summary>
/// An object store on OBS through the S3 API (AWSSDK.S3, Apache-2.0). The SDK's newer integrity checksums are sent only
/// when an operation requires them, since S3-compatible stores do not all support them.
/// </summary>
public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly ObsOptions _settings;
    private readonly AmazonS3Client _client;

    public S3ObjectStore(IOptions<ObsOptions> options)
    {
        _settings = options.Value;
        if (string.IsNullOrWhiteSpace(_settings.Bucket) || string.IsNullOrWhiteSpace(_settings.AccessKey) || string.IsNullOrWhiteSpace(_settings.SecretKey))
        {
            throw new InvalidOperationException("Huawei:Obs:Bucket, AccessKey and SecretKey must be configured, the keys from the secret store.");
        }

        _client = new AmazonS3Client(new BasicAWSCredentials(_settings.AccessKey, _settings.SecretKey), new AmazonS3Config
        {
            ServiceURL = _settings.ServiceUrl,
            AuthenticationRegion = _settings.Region,
            ForcePathStyle = _settings.ForcePathStyle,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
    }

    public async Task Put(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content, writable: false);
        await _client.PutObjectAsync(new PutObjectRequest { BucketName = _settings.Bucket, Key = key, InputStream = stream, ContentType = contentType }, cancellationToken);
    }

    public async Task<byte[]?> Get(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetObjectAsync(_settings.Bucket, key, cancellationToken);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task Delete(string key, CancellationToken cancellationToken) => _client.DeleteObjectAsync(_settings.Bucket, key, cancellationToken);

    public void Dispose() => _client.Dispose();
}
