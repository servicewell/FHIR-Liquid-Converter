// -------------------------------------------------------------------------------------------------
// Copyright (c) Service Well AB.
// Modifications licensed under the Apache License, Version 2.0. See LICENSE in the repo root.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using Firely.Fhir.Packages;
using Firely.Fhir.Validation;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Specification.Terminology;
using Microsoft.Health.Fhir.Liquid.Converter.Exceptions;
using Microsoft.Health.Fhir.Liquid.Converter.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using FhirModel = Hl7.Fhir.Model;

namespace Microsoft.Health.Fhir.Liquid.Converter.Validators;

/// <summary>
/// Validates FHIR resources against defined profiles using the FHIR validation framework.
/// Supports validation against multiple FHIR Implementation Guide (IG) sources and packages.
/// </summary>
public static class ProfileValidator
{
    private static readonly ConcurrentDictionary<string, Lazy<ValidatorContext>> _validatorCache = new();

    /// <summary>
    /// Validates that a FHIR resource conforms to its declared profiles.
    /// If the resource is a Bundle, validates the bundle itself (if it has a profile) and then validates each entry.
    /// Otherwise, validates the single resource against its declared profiles.
    /// The resource must contain at least one profile declaration in its Meta.Profile element (or be a Bundle).
    /// </summary>
    /// <param name="resource">The FHIR resource or bundle to validate against its declared profiles</param>
    /// <param name="fhirCacheDirectory">The directory path to the FHIR cache. If null, the default FHIR package root is used.</param>
    /// <param name="original">The original JObject representation of the resource, used for error reporting in case of validation failure</param>
    /// <exception cref="ArgumentNullException">Thrown when the resource parameter is null</exception>
    /// <exception cref="PostprocessException">Thrown when the resource does not contain a Meta.Profile declaration or fails validation against its declared profiles</exception>
    /// <remarks>
    /// Note: The first validation call takes approximately 2.5 seconds due to initialization overhead.
    /// Subsequent calls benefit from caching and typically complete in 10-400ms depending on resource complexity.
    /// For Bundles, each entry is validated individually, and validation errors include the entry index for easier debugging.
    /// Concurrent validations against the same cache directory are serialized because the underlying validation pipeline is not thread-safe.
    /// </remarks>
    public static void Validate(FhirModel.Resource resource, string fhirCacheDirectory = null, JObject original = null)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var cacheKey = string.IsNullOrWhiteSpace(fhirCacheDirectory)
            ? Platform.GetFhirPackageRoot()
            : fhirCacheDirectory;

        if (!Directory.Exists(cacheKey))
        {
            throw new DirectoryNotFoundException($"The specified FHIR cache directory does not exist: {cacheKey}");
        }

        var validatorContext = GetValidatorContext(cacheKey);

        lock (validatorContext.SyncRoot)
        {
            if (resource is FhirModel.Bundle bundle)
            {
                ValidateBundle(validatorContext.Validator, bundle, original);
                return;
            }

            ValidateSingleResource(validatorContext.Validator, resource, original);
        }
    }

    /// <summary>
    /// Creates or retrieves the validator context for the specified cache directory.
    /// </summary>
    /// <param name="cacheKey">The cache directory key.</param>
    /// <returns>A validator context containing the shared validator instance and synchronization object.</returns>
    private static ValidatorContext GetValidatorContext(string cacheKey)
    {
        var lazyContext = _validatorCache.GetOrAdd(
            cacheKey,
            static key => new Lazy<ValidatorContext>(
                () => CreateValidatorContext(key),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return lazyContext.Value;
    }

    /// <summary>
    /// Creates a new validator context configured with a DirectorySource for the specified directory.
    /// </summary>
    /// <param name="directory">The directory path to the FHIR cache</param>
    /// <returns>A configured validator context.</returns>
    private static ValidatorContext CreateValidatorContext(string directory)
    {
        // Even though this method sets up the validator, it is pretty fast as the profiles are lazy loaded
        var sourceSettings = new DirectorySourceSettings
        {
            IncludeSubDirectories = true,
            MultiThreaded = true,
            Mask = "*.json",
        };

        var source = new DirectorySource(directory, sourceSettings);
        var resolver = new CachedResolver(new SnapshotSource(source));
        var validator = new Validator(resolver, new LocalTerminologyService(resolver));

        return new ValidatorContext(validator);
    }

    /// <summary>
    /// Validates a FHIR Bundle and all its entries.
    /// The bundle itself is validated if it has a declared profile. All entries in the bundle are always validated.
    /// </summary>
    /// <param name="validator">The FHIR validator to use for validation</param>
    /// <param name="bundle">The FHIR Bundle to validate</param>
    /// <param name="original">The original JObject representation of the bundle, used for error reporting in case of validation failure</param>
    /// <exception cref="PostprocessException">Thrown when bundle validation fails or when an entry fails validation.</exception>
    private static void ValidateBundle(Validator validator, FhirModel.Bundle bundle, JObject original)
    {
        // Validate the bundle itself if it has a profile
        if (bundle.Meta?.Profile?.Any() ?? false)
        {
            ValidateSingleResource(validator, bundle, original);
            return;
        }

        if (bundle.Entry == null || bundle.Entry.Count == 0)
        {
            return;
        }

        // Validate each entry in the bundle
        for (int i = 0; i < bundle.Entry.Count; i++)
        {
            var entry = bundle.Entry[i];
            if (entry.Resource == null)
            {
                continue;
            }

            try
            {
                ValidateSingleResource(validator, entry.Resource, original);
            }
            catch (PostprocessException ex)
            {
                throw new PostprocessException(
                    ex.FhirConverterErrorCode,
                    $"Validation failed for bundle entry {i}: {ex.Message}",
                    original?.ToString(Formatting.Indented) ?? string.Empty,
                    ex);
            }
        }
    }

    /// <summary>
    /// Validates a single FHIR resource against its declared profiles.
    /// </summary>
    /// <param name="validator">The FHIR validator to use for validation</param>
    /// <param name="resourceToValidate">The FHIR resource to validate</param>
    /// <param name="original">The original JObject representation of the resource, used for error reporting in case of validation failure</param>
    /// <exception cref="PostprocessException">Thrown when the resource does not contain a Meta.Profile declaration or fails validation against its declared profiles</exception>
    private static void ValidateSingleResource(Validator validator, FhirModel.Resource resourceToValidate, JObject original)
    {
        if ((resourceToValidate.Meta?.Profile?.Any() ?? false) == false)
        {
            Console.Error.WriteLine($"Warning: Skipping validation for {resourceToValidate.TypeName} resource (id: {resourceToValidate.Id ?? "unknown"}) - no Meta.Profile declaration found.");
            return;
        }

        // If there is no warm up beforehand then this is the only method that typically takes around 2.5 seconds due to the initialization
        // of the validator and loading of profiles. After the first call,
        // the validator is cached and subsequent calls are much faster (10-400ms depending on resource complexity).
        var result = validator.Validate(resourceToValidate);
        if (result.Success)
        {
            return;
        }

        throw new PostprocessException(
            FhirConverterErrorCode.InvalidByProfileError,
            string.Format(Resources.InvalidByProfileError, result.ToString()),
            original?.ToString(Formatting.Indented) ?? string.Empty);
    }

    private sealed class ValidatorContext
    {
        public ValidatorContext(Validator validator)
        {
            ArgumentNullException.ThrowIfNull(validator);

            Validator = validator;
            SyncRoot = new object();
        }

        public object SyncRoot { get; }

        public Validator Validator { get; }
    }
}