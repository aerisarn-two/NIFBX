//
// What the Autodesk FBX SDK makes of a file this project wrote.
//
// The point of it is that it is not us. A NIF -> FBX -> NIF comparison cannot
// see a fault the reader and the writer share, and several have hidden there:
// a missing CreationTime record that made every file unreadable, rotations
// applied transposed, tangents taken from the wrong end of a segment. Each was
// symmetric, so the round trip closed over it and the suite stayed green.
//
//   fbxprobe where <file.fbx>          every mesh's control points in world space
//   fbxprobe curve <file.fbx> <node>   a node's rotation curves, keys and values
//
// Output is one record per line, `key=value` separated by spaces, meant to be
// parsed. Exit code 0 when the file loaded, 1 when the SDK refused it.
//
#include <fbxsdk.h>
#include <cstdio>
#include <cstring>
#include <cfloat>

static void Where(FbxNode* node)
{
    if (FbxMesh* mesh = node->GetMesh())
    {
        FbxAMatrix global = node->EvaluateGlobalTransform();

        // The offset a mesh attribute carries independently of its node, which the
        // SDK applies and a naive reader forgets.
        FbxAMatrix geometry;
        geometry.SetT(node->GetGeometricTranslation(FbxNode::eSourcePivot));
        geometry.SetR(node->GetGeometricRotation(FbxNode::eSourcePivot));
        geometry.SetS(node->GetGeometricScaling(FbxNode::eSourcePivot));

        FbxAMatrix place = global * geometry;

        double lo[3] = { DBL_MAX, DBL_MAX, DBL_MAX };
        double hi[3] = { -DBL_MAX, -DBL_MAX, -DBL_MAX };

        for (int i = 0; i < mesh->GetControlPointsCount(); ++i)
        {
            FbxVector4 p = place.MultT(mesh->GetControlPointAt(i));

            for (int a = 0; a < 3; ++a)
            {
                if (p[a] < lo[a]) lo[a] = p[a];
                if (p[a] > hi[a]) hi[a] = p[a];
            }
        }

        printf("mesh name=%s points=%d visible=%d"
               " min=%.4f,%.4f,%.4f max=%.4f,%.4f,%.4f\n",
               node->GetName(), mesh->GetControlPointsCount(),
               node->GetVisibility() ? 1 : 0,
               lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]);
    }

    for (int i = 0; i < node->GetChildCount(); ++i)
        Where(node->GetChild(i));
}

static void Curve(FbxNode* node, FbxAnimLayer* layer, const char* want)
{
    if (strcmp(node->GetName(), want) == 0)
    {
        const char* channel[] = { FBXSDK_CURVENODE_COMPONENT_X,
                                  FBXSDK_CURVENODE_COMPONENT_Y,
                                  FBXSDK_CURVENODE_COMPONENT_Z };

        for (int c = 0; c < 3; ++c)
        {
            FbxAnimCurve* curve = node->LclRotation.GetCurve(layer, channel[c]);

            if (!curve || curve->KeyGetCount() == 0) continue;

            int last = curve->KeyGetCount() - 1;
            double span = curve->KeyGet(last).GetTime().GetSecondDouble();

            for (int k = 0; k < curve->KeyGetCount(); ++k)
            {
                printf("key node=%s axis=%c index=%d time=%.6f value=%.6f"
                       " right=%.6f left=%.6f\n",
                       node->GetName(), "XYZ"[c], k,
                       curve->KeyGet(k).GetTime().GetSecondDouble(),
                       curve->KeyGet(k).GetValue(),
                       curve->KeyGetRightDerivative(k), curve->KeyGetLeftDerivative(k));
            }

            // Sampled across the curve, which is what says whether it eases where the
            // file meant it to run straight.
            for (int q = 0; q <= 4; ++q)
            {
                FbxTime at;
                at.SetSecondDouble(span * q / 4.0);

                printf("sample node=%s axis=%c time=%.6f value=%.6f\n",
                       node->GetName(), "XYZ"[c], span * q / 4.0, curve->Evaluate(at));
            }
        }
    }

    for (int i = 0; i < node->GetChildCount(); ++i)
        Curve(node->GetChild(i), layer, want);
}

int main(int argc, char** argv)
{
    if (argc < 3)
    {
        fprintf(stderr, "usage: fbxprobe where <file.fbx>\n"
                        "       fbxprobe curve <file.fbx> <nodeName>\n");
        return 2;
    }

    const char* what = argv[1];
    const char* path = argv[2];

    FbxManager* manager = FbxManager::Create();
    manager->SetIOSettings(FbxIOSettings::Create(manager, IOSROOT));

    FbxImporter* importer = FbxImporter::Create(manager, "");

    if (!importer->Initialize(path, -1, manager->GetIOSettings()))
    {
        printf("rejected reason=%s\n", importer->GetStatus().GetErrorString());
        manager->Destroy();
        return 1;
    }

    FbxScene* scene = FbxScene::Create(manager, "scene");

    if (!importer->Import(scene))
    {
        printf("rejected reason=%s\n", importer->GetStatus().GetErrorString());
        manager->Destroy();
        return 1;
    }

    FbxAxisSystem axis = scene->GetGlobalSettings().GetAxisSystem();
    int upSign = 0, frontSign = 0;
    int up = (int)axis.GetUpVector(upSign);
    int front = (int)axis.GetFrontVector(frontSign);

    printf("scene up=%d upSign=%d front=%d frontSign=%d handedness=%d units=%.6f\n",
           up, upSign, front, frontSign, (int)axis.GetCoorSystem(),
           scene->GetGlobalSettings().GetSystemUnit().GetScaleFactor());

    if (strcmp(what, "where") == 0)
    {
        Where(scene->GetRootNode());
    }
    else if (strcmp(what, "curve") == 0 && argc >= 4)
    {
        if (FbxAnimStack* stack = scene->GetSrcObject<FbxAnimStack>(0))
        {
            scene->SetCurrentAnimationStack(stack);

            printf("stack name=%s start=%.6f stop=%.6f\n", stack->GetName(),
                   stack->GetLocalTimeSpan().GetStart().GetSecondDouble(),
                   stack->GetLocalTimeSpan().GetStop().GetSecondDouble());

            Curve(scene->GetRootNode(), stack->GetMember<FbxAnimLayer>(0), argv[3]);
        }
    }

    manager->Destroy();
    return 0;
}
