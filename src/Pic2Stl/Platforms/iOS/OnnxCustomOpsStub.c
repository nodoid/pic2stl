/*
 * ONNX Runtime's static iOS build references RegisterCustomOps but leaves it for the host app to
 * provide (it is only used for mobile custom-operator packages, which this app does not use).
 * Returning NULL means "no custom operators registered" and no error.
 *
 * Compiled at build time by the CompileOnnxCustomOpsStub target in Pic2Stl.csproj.
 */

typedef struct OrtStatus OrtStatus;
typedef struct OrtSessionOptions OrtSessionOptions;
typedef struct OrtApiBase OrtApiBase;

__attribute__((visibility("default")))
OrtStatus* RegisterCustomOps(OrtSessionOptions* options, const OrtApiBase* api)
{
    (void)options;
    (void)api;
    return 0;
}
