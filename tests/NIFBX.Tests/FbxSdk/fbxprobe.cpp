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
//   fbxprobe bones <file.fbx>          the skeleton, as an importer would build it
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

// Whether the SDK sees a node as a skeleton limb, which is what decides whether
// an importer builds an armature or a pile of empties.
static void Bones(FbxNode* node, int depth)
{
    FbxNodeAttribute* attribute = node->GetNodeAttribute();

    bool limb = attribute
        && attribute->GetAttributeType() == FbxNodeAttribute::eSkeleton;

    if (limb)
    {
        FbxNode* parent = node->GetParent();

        // The dedicated accessor, and the skeleton kind it reports. eRoot is the
        // top of a chain, eLimb a bone with a length, eLimbNode a bone that is just
        // a joint, eEffector an IK target.
        FbxSkeleton* skeleton = node->GetSkeleton();
        const char* kind = "?";

        if (skeleton)
        {
            switch (skeleton->GetSkeletonType())
            {
            case FbxSkeleton::eRoot:      kind = "Root"; break;
            case FbxSkeleton::eLimb:      kind = "Limb"; break;
            case FbxSkeleton::eLimbNode:  kind = "LimbNode"; break;
            case FbxSkeleton::eEffector:  kind = "Effector"; break;
            default: break;
            }
        }

        printf("bone name=%s depth=%d parent=%s getSkeleton=%s kind=%s size=%.3f\n",
               node->GetName(), depth, parent ? parent->GetName() : "-",
               skeleton ? "yes" : "no", kind, skeleton ? skeleton->Size.Get() : 0.0);
    }

    for (int i = 0; i < node->GetChildCount(); ++i)
        Bones(node->GetChild(i), depth + 1);
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


// Every mesh's UV set, as the SDK resolves it. Which is the question a NIF -> FBX
// -> Blender trip raises and cannot answer for itself: Blender only knows the
// spelling `ByVertice` for a per-vertex layer, the FBX spec also allows
// `ByControlPoint`, and asking a reader that is neither says whether a file
// written for one is still readable by the other.
static void Uvs(FbxNode* node)
{
    if (FbxMesh* mesh = node->GetMesh())
    {
        // By index as well as by name: a lookup that fails by name says nothing
        // about whether the element was parsed at all.
        printf("uvcount node=%s elements=%d normals=%d colors=%d\n",
               node->GetName(), mesh->GetElementUVCount(),
               mesh->GetElementNormalCount(), mesh->GetElementVertexColorCount());

        for (int e = 0; e < mesh->GetElementUVCount(); ++e)
        {
            const FbxGeometryElementUV* byIndex = mesh->GetElementUV(e);
            printf("uvbyindex node=%s slot=%d name=%s mode=%d direct=%d index=%d\n",
                   node->GetName(), e, byIndex->GetName(),
                   (int)byIndex->GetMappingMode(),
                   byIndex->GetDirectArray().GetCount(),
                   byIndex->GetIndexArray().GetCount());
        }

        FbxStringList names;
        mesh->GetUVSetNames(names);

        for (int s = 0; s < names.GetCount(); ++s)
        {
            const char* set = names.GetStringAt(s);
            double lowU = 1e30, highU = -1e30, lowV = 1e30, highV = -1e30;
            int read = 0;

            for (int p = 0; p < mesh->GetPolygonCount(); ++p)
            {
                for (int v = 0; v < mesh->GetPolygonSize(p); ++v)
                {
                    FbxVector2 value;
                    bool unmapped = false;

                    if (!mesh->GetPolygonVertexUV(p, v, set, value, unmapped) || unmapped)
                        continue;

                    lowU = value[0] < lowU ? value[0] : lowU;
                    highU = value[0] > highU ? value[0] : highU;
                    lowV = value[1] < lowV ? value[1] : lowV;
                    highV = value[1] > highV ? value[1] : highV;
                    ++read;
                }
            }

            const FbxGeometryElementUV* element = mesh->GetElementUV(set);
            const char* mapping = "none";
            int mode = -1, direct = 0, indexes = 0;
            double dlowU = 1e30, dhighU = -1e30, dlowV = 1e30, dhighV = -1e30;

            if (element)
            {
                mode = (int)element->GetMappingMode();

                switch (element->GetMappingMode())
                {
                    case FbxGeometryElement::eByControlPoint: mapping = "ByControlPoint"; break;
                    case FbxGeometryElement::eByPolygonVertex: mapping = "ByPolygonVertex"; break;
                    case FbxGeometryElement::eByPolygon: mapping = "ByPolygon"; break;
                    case FbxGeometryElement::eAllSame: mapping = "AllSame"; break;
                    case FbxGeometryElement::eNone: mapping = "eNone"; break;
                    default: mapping = "other"; break;
                }

                // Straight off the element, rather than through
                // GetPolygonVertexUV, which resolves only some mapping modes.
                direct = element->GetDirectArray().GetCount();
                indexes = element->GetIndexArray().GetCount();

                for (int i = 0; i < direct; ++i)
                {
                    FbxVector2 value = element->GetDirectArray().GetAt(i);
                    dlowU = value[0] < dlowU ? value[0] : dlowU;
                    dhighU = value[0] > dhighU ? value[0] : dhighU;
                    dlowV = value[1] < dlowV ? value[1] : dlowV;
                    dhighV = value[1] > dhighV ? value[1] : dhighV;
                }
            }

            printf("uv node=%s set=%s mapping=%s(%d) direct=%d index=%d polyRead=%d "
                   "directU=%.4f..%.4f directV=%.4f..%.4f\n",
                   node->GetName(), set, mapping, mode, direct, indexes, read,
                   direct ? dlowU : 0.0, direct ? dhighU : 0.0,
                   direct ? dlowV : 0.0, direct ? dhighV : 0.0);
        }
    }

    for (int i = 0; i < node->GetChildCount(); ++i)
        Uvs(node->GetChild(i));
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
    else if (strcmp(what, "uv") == 0)
    {
        Uvs(scene->GetRootNode());
    }
    else if (strcmp(what, "bones") == 0)
    {
        Bones(scene->GetRootNode(), 0);

        // The other thing that makes a node a bone: something deforms with it.
        for (int i = 0; i < scene->GetSrcObjectCount<FbxGeometry>(); ++i)
        {
            FbxGeometry* geometry = scene->GetSrcObject<FbxGeometry>(i);

            for (int d = 0; d < geometry->GetDeformerCount(FbxDeformer::eSkin); ++d)
            {
                FbxSkin* skin = (FbxSkin*)geometry->GetDeformer(d, FbxDeformer::eSkin);

                for (int c = 0; c < skin->GetClusterCount(); ++c)
                {
                    FbxNode* link = skin->GetCluster(c)->GetLink();

                    if (!link) continue;

                    printf("link name=%s hasSkeletonAttribute=%d\n",
                           link->GetName(), link->GetSkeleton() ? 1 : 0);
                }
            }
        }
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
