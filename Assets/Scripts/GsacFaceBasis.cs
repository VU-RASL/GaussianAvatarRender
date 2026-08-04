using System;
using System.IO;
using System.Text;
using UnityEngine;

public sealed class GsacFaceBasis
{
    public const string Magic = "GSACFB01";
    public const int ExpectedVersion = 1;
    public const int ExpectedVertexCount = 10475;
    public const int ExpectedExpressionCount = 50;
    public const int ExpectedEyelidCount = 2;
    public const int EyelidLeftControlIndex = ExpectedExpressionCount;
    public const int EyelidRightControlIndex = ExpectedExpressionCount + 1;

    const int HeaderSize = 24;
    const float MappingPositionTolerance = 1e-6f;
    const float MappingExpressionTolerance = 1e-5f;
    const float MappingMovementThreshold = 1e-8f;

    readonly byte[] bytes;

    public int version { get; private set; }
    public int vertexCount { get; private set; }
    public int expressionCount { get; private set; }
    public int eyelidCount { get; private set; }
    public string sourceName { get; private set; }
    public bool mirrorXToUnity { get; private set; }
    public float deltaScaleToUnity { get; private set; } = 1.0f;

    GsacFaceBasis(byte[] bytes, int version, int vertexCount, int expressionCount, int eyelidCount, string sourceName)
    {
        this.bytes = bytes;
        this.version = version;
        this.vertexCount = vertexCount;
        this.expressionCount = expressionCount;
        this.eyelidCount = eyelidCount;
        this.sourceName = sourceName;
    }

    public static bool TryLoad(byte[] sourceBytes, string sourceName, out GsacFaceBasis basis, out string error)
    {
        basis = null;
        error = string.Empty;

        if (sourceBytes == null)
        {
            error = "Face basis bytes are null.";
            return false;
        }

        if (sourceBytes.Length < HeaderSize)
        {
            error = $"Face basis is too short: {sourceBytes.Length} bytes.";
            return false;
        }

        string magic = Encoding.ASCII.GetString(sourceBytes, 0, 8);
        if (magic != Magic)
        {
            error = $"Face basis magic mismatch. Expected {Magic}, got {magic}.";
            return false;
        }

        int version = (int)ReadUInt32LittleEndian(sourceBytes, 8);
        int vertexCount = (int)ReadUInt32LittleEndian(sourceBytes, 12);
        int expressionCount = (int)ReadUInt32LittleEndian(sourceBytes, 16);
        int eyelidCount = (int)ReadUInt32LittleEndian(sourceBytes, 20);

        if (version != ExpectedVersion)
        {
            error = $"Unsupported face basis version {version}; expected {ExpectedVersion}.";
            return false;
        }

        if (vertexCount != ExpectedVertexCount)
        {
            error = $"Unexpected face basis vertex count {vertexCount}; expected {ExpectedVertexCount}.";
            return false;
        }

        if (expressionCount != ExpectedExpressionCount || eyelidCount != ExpectedEyelidCount)
        {
            error = $"Unexpected face basis dimensions: expressions={expressionCount}, eyelids={eyelidCount}.";
            return false;
        }

        long expectedLength = HeaderSize + (long)(expressionCount + eyelidCount) * vertexCount * 3 * sizeof(float);
        if (sourceBytes.LongLength != expectedLength)
        {
            error = $"Face basis byte length mismatch. Expected {expectedLength}, got {sourceBytes.LongLength}.";
            return false;
        }

        for (int offset = HeaderSize; offset < sourceBytes.Length; offset += sizeof(float))
        {
            float value = ReadSingleLittleEndian(sourceBytes, offset);
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                error = $"Face basis contains a non-finite value at byte offset {offset}.";
                return false;
            }
        }

        basis = new GsacFaceBasis(sourceBytes, version, vertexCount, expressionCount, eyelidCount, sourceName);
        return true;
    }

    public static bool TryLoadFromFile(string path, out GsacFaceBasis basis, out string error)
    {
        basis = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Face basis file path is empty.";
            return false;
        }

        if (!File.Exists(path))
        {
            error = $"Face basis file was not found: {path}";
            return false;
        }

        try
        {
            return TryLoad(File.ReadAllBytes(path), path, out basis, out error);
        }
        catch (Exception exception)
        {
            error = $"Failed to read face basis file: {exception.Message}";
            return false;
        }
    }

    public bool TryInstallOn(SkinnedMeshRenderer renderer, out string message)
    {
        message = string.Empty;

        if (renderer == null)
        {
            message = "Cannot install face basis because SkinnedMeshRenderer is missing.";
            return false;
        }

        Mesh mesh = renderer.sharedMesh;
        if (mesh == null)
        {
            message = "Cannot install face basis because the renderer has no shared mesh.";
            return false;
        }

        int targetVertexCount = mesh.vertexCount;
        if (targetVertexCount < vertexCount)
        {
            message = $"Face basis topology mismatch. Mesh has {targetVertexCount} vertices, basis has {vertexCount}.";
            return false;
        }

        if (!TryBuildTargetToSourceMapping(mesh, out int[] targetToSource, out bool mirrorX, out float deltaScale, out string mappingMessage))
        {
            message = mappingMessage;
            return false;
        }

        mirrorXToUnity = mirrorX;
        deltaScaleToUnity = deltaScale;

        bool needsAppend = false;
        for (int i = 10; i < expressionCount; ++i)
        {
            if (FindBlendShapeIndex(mesh, ExpressionName(i)) < 0)
            {
                needsAppend = true;
                break;
            }
        }

        if (!needsAppend)
        {
            needsAppend = FindBlendShapeIndex(mesh, EyelidLeftName) < 0 ||
                          FindBlendShapeIndex(mesh, EyelidRightName) < 0;
        }

        if (!needsAppend)
        {
            message = $"Face basis already present on {mesh.name}. {mappingMessage}";
            return true;
        }

        Mesh clonedMesh = UnityEngine.Object.Instantiate(mesh);
        clonedMesh.name = mesh.name.EndsWith("_GsacFaceBasis", StringComparison.Ordinal)
            ? mesh.name
            : $"{mesh.name}_GsacFaceBasis";

        Vector3[] deltaVertices = new Vector3[targetVertexCount];

        try
        {
            for (int i = 10; i < expressionCount; ++i)
            {
                string blendShapeName = ExpressionName(i);
                if (FindBlendShapeIndex(clonedMesh, blendShapeName) >= 0)
                    continue;

                FillDeltaVertices(i, deltaVertices, mirrorX, deltaScale, targetToSource);
                clonedMesh.AddBlendShapeFrame(blendShapeName, 100.0f, deltaVertices, null, null);
            }

            AppendEyelidIfMissing(clonedMesh, EyelidLeftControlIndex, EyelidLeftName, deltaVertices, mirrorX, deltaScale, targetToSource);
            AppendEyelidIfMissing(clonedMesh, EyelidRightControlIndex, EyelidRightName, deltaVertices, mirrorX, deltaScale, targetToSource);

            renderer.sharedMesh = clonedMesh;
            message = $"Installed 50-expression/2-eyelid face basis on {renderer.name}. {mappingMessage}";
            return true;
        }
        catch (Exception exception)
        {
            DestroyMesh(clonedMesh);
            message = $"Failed while appending face basis blendshapes: {exception.Message}";
            return false;
        }
    }

    void AppendEyelidIfMissing(Mesh mesh, int controlIndex, string blendShapeName, Vector3[] deltaVertices, bool mirrorX, float deltaScale, int[] targetToSource)
    {
        if (FindBlendShapeIndex(mesh, blendShapeName) >= 0)
            return;

        FillDeltaVertices(controlIndex, deltaVertices, mirrorX, deltaScale, targetToSource);
        mesh.AddBlendShapeFrame(blendShapeName, 100.0f, deltaVertices, null, null);
    }

    bool TryBuildTargetToSourceMapping(Mesh mesh, out int[] targetToSource, out bool mirrorX, out float deltaScale, out string message)
    {
        message = string.Empty;
        targetToSource = null;
        mirrorX = false;
        deltaScale = 1.0f;

        if (!TryLoadExistingExpressionDeltas(mesh, out Vector3[][] existingDeltas, out message))
            return false;

        PrefixValidation noMirrorPrefix = EvaluatePrefixValidation(existingDeltas, false);
        PrefixValidation mirrorPrefix = EvaluatePrefixValidation(existingDeltas, true);
        PrefixValidation bestPrefix = mirrorPrefix.vectorRms < noMirrorPrefix.vectorRms ? mirrorPrefix : noMirrorPrefix;
        bool prefixExact = bestPrefix.mismatchedVertices == 0 && bestPrefix.maxError <= MappingExpressionTolerance;

        if (!prefixExact)
        {
            int futureOnlySources = CountSourceFutureOnlyMovingVertices();
            if (futureOnlySources > 0)
            {
                message =
                    $"Cannot full-map face basis topology because {futureOnlySources} source vertices move only in Exp010..Exp049/eyelids. " +
                    $"{FormatPrefixValidation(bestPrefix)}";
                return false;
            }

            FullMappingCandidate noMirrorCandidate = BuildFullExpressionMapping(existingDeltas, false, 1.0f);
            FullMappingCandidate mirrorCandidate = BuildFullExpressionMapping(existingDeltas, true, 1.0f);
            FullMappingCandidate scaledNoMirrorCandidate = BuildFullExpressionMapping(existingDeltas, false, noMirrorPrefix.deltaScale);
            FullMappingCandidate scaledMirrorCandidate = BuildFullExpressionMapping(existingDeltas, true, mirrorPrefix.deltaScale);
            FullMappingCandidate bestCandidate = ChooseBestFullMapping(
                ChooseBestFullMapping(noMirrorCandidate, mirrorCandidate),
                ChooseBestFullMapping(scaledNoMirrorCandidate, scaledMirrorCandidate));
            if (bestCandidate.unmappedMovingTargets > 0)
            {
                message =
                    $"Cannot full-map face basis topology: {bestCandidate.unmappedMovingTargets}/{bestCandidate.movingTargets} moving target vertices were unmapped. " +
                    $"{FormatPrefixValidation(bestPrefix)} Full map mirrorX={bestCandidate.mirrorX}, deltaScale={bestCandidate.deltaScale:0.########}, " +
                    $"max Exp000..Exp009 error={bestCandidate.maxExpressionError:0.########}.";
                return false;
            }

            targetToSource = bestCandidate.targetToSource;
            mirrorX = bestCandidate.mirrorX;
            deltaScale = bestCandidate.deltaScale;
            message =
                $"Basis topology used full expression-delta mapping for non-prefix mesh layout: " +
                $"mapped {bestCandidate.mappedMovingTargets}/{bestCandidate.movingTargets} moving target vertices; " +
                $"{bestCandidate.staticTargets} static target vertices left independent/static; " +
                $"mirrorX={mirrorX}; deltaScale={deltaScale:0.########}; max Exp000..Exp009 map error={bestCandidate.maxExpressionError:0.########}, " +
                $"mean error={bestCandidate.meanExpressionError:0.########}. {FormatPrefixValidation(bestPrefix)}";
            return true;
        }

        int targetVertexCount = mesh.vertexCount;
        targetToSource = new int[targetVertexCount];
        for (int i = 0; i < targetToSource.Length; ++i)
            targetToSource[i] = i < vertexCount ? i : -1;
        mirrorX = bestPrefix.mirrorX;
        deltaScale = bestPrefix.deltaScale;

        if (targetVertexCount == vertexCount)
        {
            message = $"Basis topology exact prefix match at {vertexCount} vertices. {FormatPrefixValidation(bestPrefix)}";
            return true;
        }

        Vector3[] vertices = mesh.vertices;
        int extraVertexCount = targetVertexCount - vertexCount;
        int mappedExtraVertices = 0;
        int mappedMovingExtraVertices = 0;
        int mappedStaticExtraVertices = 0;
        int independentStaticExtraVertices = 0;
        double maxPositionDistance = 0.0;
        double maxExpressionError = 0.0;

        float positionToleranceSquared = MappingPositionTolerance * MappingPositionTolerance;
        for (int target = vertexCount; target < targetVertexCount; ++target)
        {
            int bestSource = -1;
            double bestExpressionError = double.PositiveInfinity;
            float bestPositionSquared = float.PositiveInfinity;

            for (int source = 0; source < vertexCount; ++source)
            {
                float positionSquared = (vertices[target] - vertices[source]).sqrMagnitude;
                if (positionSquared > positionToleranceSquared)
                    continue;

                double expressionError = ExistingExpressionDeltaError(existingDeltas, target, source);
                if (expressionError < bestExpressionError)
                {
                    bestExpressionError = expressionError;
                    bestPositionSquared = positionSquared;
                    bestSource = source;
                }
            }

            bool moving = HasAnyExistingExpressionMovement(existingDeltas, target);
            if (bestSource >= 0 && bestExpressionError <= MappingExpressionTolerance)
            {
                targetToSource[target] = bestSource;
                ++mappedExtraVertices;
                if (moving)
                    ++mappedMovingExtraVertices;
                else
                    ++mappedStaticExtraVertices;

                maxPositionDistance = Math.Max(maxPositionDistance, Math.Sqrt(bestPositionSquared));
                maxExpressionError = Math.Max(maxExpressionError, bestExpressionError);
                continue;
            }

            if (moving)
            {
                message = $"Cannot map moving extra vertex {target}. Closest position/source={bestSource}, expression error={bestExpressionError:0.########}.";
                targetToSource = null;
                return false;
            }

            ++independentStaticExtraVertices;
        }

        message =
            $"Basis topology mapped {mappedExtraVertices}/{extraVertexCount} extra vertices to source SMPL-X vertices " +
            $"({mappedMovingExtraVertices} moving, {mappedStaticExtraVertices} static); " +
            $"{independentStaticExtraVertices} extra vertices left independent/static. " +
            $"Max map distance={maxPositionDistance:0.########}, max Exp000..Exp009 map error={maxExpressionError:0.########}. " +
            $"{FormatPrefixValidation(bestPrefix)}";
        return true;
    }

    PrefixValidation EvaluatePrefixValidation(Vector3[][] existingDeltas, bool mirrorX)
    {
        PrefixValidation validation = new PrefixValidation();
        validation.mirrorX = mirrorX;

        double sourceEnergy = 0.0;
        double targetSourceDot = 0.0;
        for (int control = 0; control < 10; ++control)
        {
            for (int vertex = 0; vertex < vertexCount; ++vertex)
            {
                Vector3 target = existingDeltas[control][vertex];
                Vector3 source = ReadDelta(control, vertex, mirrorX);
                sourceEnergy += SquaredMagnitude(source);
                targetSourceDot += Dot(target, source);
            }
        }

        double estimatedScale = sourceEnergy > 1e-16 ? targetSourceDot / sourceEnergy : 1.0;
        if (double.IsNaN(estimatedScale) || double.IsInfinity(estimatedScale) || estimatedScale <= 0.0)
            estimatedScale = 1.0;

        validation.deltaScale = (float)estimatedScale;
        double sumSquared = 0.0;
        int comparedControlVertices = 0;
        bool[] mismatchedVertex = new bool[vertexCount];
        double thresholdSquared = MappingExpressionTolerance * MappingExpressionTolerance;

        for (int control = 0; control < 10; ++control)
        {
            for (int vertex = 0; vertex < vertexCount; ++vertex)
            {
                Vector3 error = existingDeltas[control][vertex] - ReadDelta(control, vertex, mirrorX) * validation.deltaScale;
                double squared = SquaredMagnitude(error);
                sumSquared += squared;
                ++comparedControlVertices;

                double magnitude = Math.Sqrt(squared);
                if (magnitude > validation.maxError)
                    validation.maxError = magnitude;

                if (squared > thresholdSquared)
                {
                    ++validation.mismatchedControlVertices;
                    mismatchedVertex[vertex] = true;
                }
            }
        }

        for (int vertex = 0; vertex < mismatchedVertex.Length; ++vertex)
        {
            if (mismatchedVertex[vertex])
                ++validation.mismatchedVertices;
        }

        validation.vectorRms = comparedControlVertices > 0 ? Math.Sqrt(sumSquared / comparedControlVertices) : 0.0;
        return validation;
    }

    FullMappingCandidate BuildFullExpressionMapping(Vector3[][] existingDeltas, bool mirrorX, float deltaScale)
    {
        Vector3[][] sourceDeltas = BuildSourceExpressionDeltas(mirrorX, deltaScale);
        int targetVertexCount = existingDeltas[0].Length;
        var candidate = new FullMappingCandidate(targetVertexCount);
        candidate.mirrorX = mirrorX;
        candidate.deltaScale = deltaScale;

        int[] movingSources = new int[vertexCount];
        int movingSourceCount = 0;
        for (int source = 0; source < vertexCount; ++source)
        {
            if (HasAnySourceExpressionMovement(sourceDeltas, source))
                movingSources[movingSourceCount++] = source;
        }

        double totalMappedError = 0.0;
        for (int target = 0; target < targetVertexCount; ++target)
        {
            if (!HasAnyExistingExpressionMovement(existingDeltas, target))
            {
                ++candidate.staticTargets;
                continue;
            }

            ++candidate.movingTargets;
            int bestSource = -1;
            double bestError = double.PositiveInfinity;
            for (int i = 0; i < movingSourceCount; ++i)
            {
                int source = movingSources[i];
                double error = ExistingToSourceExpressionDeltaError(existingDeltas, target, sourceDeltas, source);
                if (error < bestError)
                {
                    bestError = error;
                    bestSource = source;
                }
            }

            if (bestSource >= 0 && bestError <= MappingExpressionTolerance)
            {
                candidate.targetToSource[target] = bestSource;
                ++candidate.mappedMovingTargets;
                totalMappedError += bestError;
                if (bestError > candidate.maxExpressionError)
                    candidate.maxExpressionError = bestError;
            }
            else
            {
                ++candidate.unmappedMovingTargets;
                if (!double.IsInfinity(bestError) && bestError > candidate.maxExpressionError)
                    candidate.maxExpressionError = bestError;
            }
        }

        candidate.meanExpressionError = candidate.mappedMovingTargets > 0
            ? totalMappedError / candidate.mappedMovingTargets
            : 0.0;
        return candidate;
    }

    Vector3[][] BuildSourceExpressionDeltas(bool mirrorX, float deltaScale)
    {
        Vector3[][] sourceDeltas = new Vector3[10][];
        for (int control = 0; control < sourceDeltas.Length; ++control)
        {
            sourceDeltas[control] = new Vector3[vertexCount];
            for (int source = 0; source < vertexCount; ++source)
                sourceDeltas[control][source] = ReadDelta(control, source, mirrorX) * deltaScale;
        }

        return sourceDeltas;
    }

    FullMappingCandidate ChooseBestFullMapping(FullMappingCandidate first, FullMappingCandidate second)
    {
        if (first.unmappedMovingTargets != second.unmappedMovingTargets)
            return first.unmappedMovingTargets < second.unmappedMovingTargets ? first : second;

        if (Math.Abs(first.meanExpressionError - second.meanExpressionError) > double.Epsilon)
            return first.meanExpressionError < second.meanExpressionError ? first : second;

        return first.maxExpressionError <= second.maxExpressionError ? first : second;
    }

    bool TryLoadExistingExpressionDeltas(Mesh mesh, out Vector3[][] existingDeltas, out string message)
    {
        message = string.Empty;
        existingDeltas = new Vector3[10][];
        Vector3[] normals = new Vector3[mesh.vertexCount];
        Vector3[] tangents = new Vector3[mesh.vertexCount];

        for (int control = 0; control < existingDeltas.Length; ++control)
        {
            string blendShapeName = ExpressionName(control);
            int blendShapeIndex = FindBlendShapeIndex(mesh, blendShapeName);
            if (blendShapeIndex < 0)
            {
                message = $"Cannot map face basis: required blendshape {blendShapeName} is missing.";
                return false;
            }

            int frameIndex = mesh.GetBlendShapeFrameCount(blendShapeIndex) - 1;
            if (frameIndex < 0)
            {
                message = $"Cannot map face basis: blendshape {blendShapeName} has no frames.";
                return false;
            }

            existingDeltas[control] = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(blendShapeIndex, frameIndex, existingDeltas[control], normals, tangents);
        }

        return true;
    }

    double ExistingExpressionDeltaError(Vector3[][] existingDeltas, int targetVertex, int sourceVertex)
    {
        double sumSquared = 0.0;
        for (int control = 0; control < existingDeltas.Length; ++control)
            sumSquared += SquaredMagnitude(existingDeltas[control][targetVertex] - existingDeltas[control][sourceVertex]);

        return Math.Sqrt(sumSquared);
    }

    bool HasAnyExistingExpressionMovement(Vector3[][] existingDeltas, int vertex)
    {
        double thresholdSquared = MappingMovementThreshold * MappingMovementThreshold;
        for (int control = 0; control < existingDeltas.Length; ++control)
        {
            if (SquaredMagnitude(existingDeltas[control][vertex]) > thresholdSquared)
                return true;
        }

        return false;
    }

    bool HasAnySourceExpressionMovement(Vector3[][] sourceDeltas, int source)
    {
        double thresholdSquared = MappingMovementThreshold * MappingMovementThreshold;
        for (int control = 0; control < sourceDeltas.Length; ++control)
        {
            if (SquaredMagnitude(sourceDeltas[control][source]) > thresholdSquared)
                return true;
        }

        return false;
    }

    int CountSourceFutureOnlyMovingVertices()
    {
        int count = 0;
        for (int source = 0; source < vertexCount; ++source)
        {
            bool firstTenMoving = false;
            for (int control = 0; control < 10; ++control)
            {
                if (SquaredMagnitude(ReadDelta(control, source, false)) > MappingMovementThreshold * MappingMovementThreshold)
                {
                    firstTenMoving = true;
                    break;
                }
            }

            if (firstTenMoving)
                continue;

            for (int control = 10; control < expressionCount + eyelidCount; ++control)
            {
                if (SquaredMagnitude(ReadDelta(control, source, false)) > MappingMovementThreshold * MappingMovementThreshold)
                {
                    ++count;
                    break;
                }
            }
        }

        return count;
    }

    double ExistingToSourceExpressionDeltaError(Vector3[][] existingDeltas, int targetVertex, Vector3[][] sourceDeltas, int sourceVertex)
    {
        double sumSquared = 0.0;
        for (int control = 0; control < existingDeltas.Length; ++control)
            sumSquared += SquaredMagnitude(existingDeltas[control][targetVertex] - sourceDeltas[control][sourceVertex]);

        return Math.Sqrt(sumSquared);
    }

    string FormatPrefixValidation(PrefixValidation validation)
    {
        return
            $"Prefix validation mirrorX={validation.mirrorX}, RMS={validation.vectorRms:0.########}, " +
            $"max={validation.maxError:0.########}, deltaScale={validation.deltaScale:0.########}, mismatchedVertices={validation.mismatchedVertices}, " +
            $"mismatchedControlVertices={validation.mismatchedControlVertices}.";
    }

    public void FillDeltaVertices(int controlIndex, Vector3[] deltaVertices, bool mirrorX)
    {
        if (deltaVertices == null || deltaVertices.Length < vertexCount)
            throw new ArgumentException($"deltaVertices must have length at least {vertexCount}.");

        if (controlIndex < 0 || controlIndex >= expressionCount + eyelidCount)
            throw new ArgumentOutOfRangeException(nameof(controlIndex));

        Array.Clear(deltaVertices, 0, deltaVertices.Length);
        for (int vertex = 0; vertex < vertexCount; ++vertex)
            deltaVertices[vertex] = ReadDelta(controlIndex, vertex, mirrorX);
    }

    void FillDeltaVertices(int controlIndex, Vector3[] deltaVertices, bool mirrorX, float deltaScale, int[] targetToSource)
    {
        if (targetToSource == null || targetToSource.Length != deltaVertices.Length)
            throw new ArgumentException("targetToSource must match deltaVertices length.");

        if (controlIndex < 0 || controlIndex >= expressionCount + eyelidCount)
            throw new ArgumentOutOfRangeException(nameof(controlIndex));

        Array.Clear(deltaVertices, 0, deltaVertices.Length);
        for (int target = 0; target < deltaVertices.Length; ++target)
        {
            int source = targetToSource[target];
            if (source >= 0)
                deltaVertices[target] = ReadDelta(controlIndex, source, mirrorX) * deltaScale;
        }
    }

    Vector3 ReadDelta(int controlIndex, int vertexIndex, bool mirrorX)
    {
        int offset = HeaderSize + ((controlIndex * vertexCount + vertexIndex) * 3 * sizeof(float));
        float x = ReadSingleLittleEndian(bytes, offset);
        float y = ReadSingleLittleEndian(bytes, offset + sizeof(float));
        float z = ReadSingleLittleEndian(bytes, offset + sizeof(float) * 2);
        if (mirrorX)
            x = -x;
        return new Vector3(x, y, z);
    }

    static double SquaredMagnitude(Vector3 value)
    {
        return (double)value.x * value.x + (double)value.y * value.y + (double)value.z * value.z;
    }

    static double Dot(Vector3 a, Vector3 b)
    {
        return (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z;
    }

    public static int FindBlendShapeIndex(Mesh mesh, string blendShapeName)
    {
        if (mesh == null || string.IsNullOrEmpty(blendShapeName))
            return -1;

        int blendShapeCount = mesh.blendShapeCount;
        for (int i = 0; i < blendShapeCount; ++i)
        {
            if (mesh.GetBlendShapeName(i) == blendShapeName)
                return i;
        }

        return -1;
    }

    public static string ExpressionName(int index)
    {
        return $"Exp{index:000}";
    }

    public const string EyelidLeftName = "GsacEyelidLeft";
    public const string EyelidRightName = "GsacEyelidRight";

    static uint ReadUInt32LittleEndian(byte[] data, int offset)
    {
        return (uint)(data[offset] |
                      (data[offset + 1] << 8) |
                      (data[offset + 2] << 16) |
                      (data[offset + 3] << 24));
    }

    static float ReadSingleLittleEndian(byte[] data, int offset)
    {
        if (BitConverter.IsLittleEndian)
            return BitConverter.ToSingle(data, offset);

        byte[] converted = new byte[4];
        converted[0] = data[offset + 3];
        converted[1] = data[offset + 2];
        converted[2] = data[offset + 1];
        converted[3] = data[offset];
        return BitConverter.ToSingle(converted, 0);
    }

    static void DestroyMesh(Mesh mesh)
    {
        if (mesh == null)
            return;

        if (Application.isPlaying)
            UnityEngine.Object.Destroy(mesh);
        else
            UnityEngine.Object.DestroyImmediate(mesh);
    }

    struct PrefixValidation
    {
        public bool mirrorX;
        public float deltaScale;
        public double vectorRms;
        public double maxError;
        public int mismatchedVertices;
        public int mismatchedControlVertices;
    }

    sealed class FullMappingCandidate
    {
        public readonly int[] targetToSource;
        public bool mirrorX;
        public float deltaScale;
        public int movingTargets;
        public int mappedMovingTargets;
        public int unmappedMovingTargets;
        public int staticTargets;
        public double maxExpressionError;
        public double meanExpressionError;

        public FullMappingCandidate(int targetVertexCount)
        {
            targetToSource = new int[targetVertexCount];
            for (int i = 0; i < targetToSource.Length; ++i)
                targetToSource[i] = -1;
        }
    }
}
