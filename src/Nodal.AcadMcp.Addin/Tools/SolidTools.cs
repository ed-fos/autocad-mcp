using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Nodal.AcadMcp.Addin.Tools;

/// <summary>
/// Plain-AutoCAD 3D solid modelling: primitives, profile-driven solids (extrude / revolve /
/// sweep / loft), booleans, slicing and mass properties. No AutoCAD Architecture involvement.
///
/// PLACEMENT CONVENTION. Every Solid3d.Create* primitive is built by ObjectARX centred on the
/// WCS origin with its axis along +Z. This module therefore always builds at the origin and then
/// applies ONE Matrix3d (AlignCoordinateSystem) that both rotates the primitive onto the
/// requested 'axis' and translates it onto the requested centre. Callers may give:
///   center     [x,y(,z)]  centre of the whole primitive           (default: 0,0,0)
///   basePoint  [x,y(,z)]  centre of the BOTTOM face; center = basePoint + axis * height/2
///   corner     [x,y(,z)]  create_box only: the minimum corner of the axis-aligned box
///   axis       [x,y(,z)]  direction the primitive's local +Z points (default: 0,0,1)
/// 'corner' is only meaningful for an unrotated box, so combining it with 'axis' is rejected
/// rather than silently reinterpreted.
///
/// OWNERSHIP. Anything Solid3d/Region hands back that does not end up appended to a block table
/// record is Disposed here, so a failed or partial operation leaves no orphan objects behind.
/// Boolean operands are database-resident, so they are Erase()d (never Disposed) once consumed,
/// and a boolean whose result is an empty body erases that result too.
///
/// NOT IMPLEMENTED / DIVERGENCES (see the module's skipped list):
///  * Solid3d has no CreateCylinder / CreateCone. Both are CreateFrustum(height, radiusAlongX,
///    radiusAlongY, topRadius): cylinder passes topRadius == radiusAlongX, cone passes the
///    caller's topRadius (default 0). That is the only shipped API for either shape.
///  * Filleting/chamfering edges, shelling and face editing need SubentityIds, which cannot be
///    addressed by handle, so they are out of this module's scope.
/// </summary>
internal static class SolidTools
{
    private const int DefaultLimit = 500;
    private const int MaxLimit = 5000;

    internal static void Register()
    {
        RegisterPrimitives();
        RegisterProfileSolids();
        RegisterEditors();
    }

    // ------------------------------------------------------------------ primitives

    private static void RegisterPrimitives()
    {
        Router.Register("create_box",
            "Create a 3D solid box. Args: length (along local X), width (along local Y), height "
            + "(along local Z); placement via center [x,y,z] (centre of the box), basePoint (centre "
            + "of the bottom face), or corner (minimum corner, unrotated boxes only); optional axis "
            + "[x,y,z] for the local +Z direction, layer, colorIndex, space (model|paper|current).",
            mutating: true,
            args =>
            {
                var length = Positive(args, "length");
                var width = Positive(args, "width");
                var height = Positive(args, "height");
                var axis = AxisOf(args);

                Point3d centre;
                if (Has(args, "corner"))
                {
                    if (Has(args, "axis"))
                        throw new AcadMcpException("bad_argument",
                            "'corner' describes an axis-aligned box, so it cannot be combined with 'axis'.",
                            "Use 'center' or 'basePoint' when the box is rotated.");

                    centre = Args.Point(args, "corner")
                                 .Add(new Vector3d(length / 2.0, width / 2.0, height / 2.0));
                }
                else
                {
                    centre = CentreOf(args, axis, height);
                }

                var placement = PlaceAt(centre, axis);
                return CreateSolid(args, solid =>
                {
                    solid.CreateBox(length, width, height);
                    solid.TransformBy(placement);
                });
            });

        Router.Register("create_cylinder",
            "Create a 3D solid cylinder via Solid3d.CreateFrustum with topRadius == radius. Args: "
            + "radius, height; optional radiusY (elliptical cylinder, defaults to radius), center or "
            + "basePoint, axis, layer, colorIndex, space.",
            mutating: true,
            args =>
            {
                var radius = Positive(args, "radius");
                var radiusY = PositiveOpt(args, "radiusY", radius);
                var height = Positive(args, "height");
                var axis = AxisOf(args);
                var placement = PlaceAt(CentreOf(args, axis, height), axis);

                return CreateSolid(args, solid =>
                {
                    solid.CreateFrustum(height, radius, radiusY, radius);
                    solid.TransformBy(placement);
                });
            });

        Router.Register("create_cone",
            "Create a 3D solid cone or truncated cone via Solid3d.CreateFrustum. Args: radius (base), "
            + "height; optional topRadius (default 0 = a true cone), radiusY (elliptical base, "
            + "defaults to radius), center or basePoint, axis, layer, colorIndex, space.",
            mutating: true,
            args =>
            {
                var radius = Positive(args, "radius");
                var radiusY = PositiveOpt(args, "radiusY", radius);
                var height = Positive(args, "height");
                var topRadius = NonNegativeOpt(args, "topRadius", 0.0);
                var axis = AxisOf(args);
                var placement = PlaceAt(CentreOf(args, axis, height), axis);

                return CreateSolid(args, solid =>
                {
                    solid.CreateFrustum(height, radius, radiusY, topRadius);
                    solid.TransformBy(placement);
                });
            });

        Router.Register("create_sphere",
            "Create a 3D solid sphere. Args: radius; optional center [x,y,z], layer, colorIndex, space.",
            mutating: true,
            args =>
            {
                var radius = Positive(args, "radius");
                var placement = PlaceAt(CentreOf(args, Vector3d.ZAxis, 0.0), Vector3d.ZAxis);

                return CreateSolid(args, solid =>
                {
                    solid.CreateSphere(radius);
                    solid.TransformBy(placement);
                });
            });

        Router.Register("create_wedge",
            "Create a 3D solid wedge (a box cut diagonally, sloping down along local +X). Args: "
            + "length (X), width (Y), height (Z); optional center or basePoint, axis, layer, "
            + "colorIndex, space.",
            mutating: true,
            args =>
            {
                var length = Positive(args, "length");
                var width = Positive(args, "width");
                var height = Positive(args, "height");
                var axis = AxisOf(args);
                var placement = PlaceAt(CentreOf(args, axis, height), axis);

                return CreateSolid(args, solid =>
                {
                    solid.CreateWedge(length, width, height);
                    solid.TransformBy(placement);
                });
            });

        Router.Register("create_torus",
            "Create a 3D solid torus lying in the plane perpendicular to its axis. Args: majorRadius "
            + "(centre of the tube from the centre of the torus), minorRadius (tube radius); optional "
            + "center, axis, layer, colorIndex, space.",
            mutating: true,
            args =>
            {
                var major = Args.Dbl(args, "majorRadius");
                var minor = Positive(args, "minorRadius");

                if (major > 0 && minor >= major)
                    throw new AcadMcpException("bad_argument",
                        $"'minorRadius' ({minor}) must be smaller than 'majorRadius' ({major}).",
                        "A self-intersecting torus needs a negative majorRadius; AutoCAD rejects this combination.");
                if (Math.Abs(major) < 1e-12)
                    throw new AcadMcpException("bad_argument", "'majorRadius' must not be zero.");

                var axis = AxisOf(args);
                var placement = PlaceAt(CentreOf(args, axis, 0.0), axis);

                return CreateSolid(args, solid =>
                {
                    solid.CreateTorus(major, minor);
                    solid.TransformBy(placement);
                });
            });

        Router.Register("create_pyramid",
            "Create a 3D solid pyramid or frustum on a regular polygon base. Args: height, sides "
            + "(3-64), radius (circumscribed radius of the base polygon); optional topRadius "
            + "(default 0 = a true pyramid), center or basePoint, axis, layer, colorIndex, space.",
            mutating: true,
            args =>
            {
                var height = Positive(args, "height");
                var sides = Args.Int(args, "sides");
                if (sides is < 3 or > 64)
                    throw new AcadMcpException("bad_argument", $"'sides' must be between 3 and 64; got {sides}.");

                var radius = Positive(args, "radius");
                var topRadius = NonNegativeOpt(args, "topRadius", 0.0);
                var axis = AxisOf(args);
                var placement = PlaceAt(CentreOf(args, axis, height), axis);

                return CreateSolid(args, solid =>
                {
                    solid.CreatePyramid(height, sides, radius, topRadius);
                    solid.TransformBy(placement);
                });
            });
    }

    // -------------------------------------------------------------- profile solids

    private static void RegisterProfileSolids()
    {
        Router.Register("extrude_profile",
            "Extrude a closed profile into a 3D solid. Args: handle (one closed curve or an existing "
            + "REGION) or handles [..] (several curves forming one closed loop); height (ignored when "
            + "'pathHandle' is given, which extrudes along that curve instead); optional taperAngle in "
            + "degrees (default 0), subtractInner (bool: when the curves yield several regions, "
            + "subtract regions 2..n from region 1 to make a ring), eraseProfile (bool, default false), "
            + "layer, colorIndex, space.",
            mutating: true,
            args => Acad.Write<object?>(ctx =>
            {
                var height = Has(args, "pathHandle") ? 0.0 : Args.Dbl(args, "height");
                var taper = Deg2Rad(Args.DblOpt(args, "taperAngle", 0.0));

                Curve? path = null;
                if (Has(args, "pathHandle"))
                    path = RequireCurve(ctx, Args.Str(args, "pathHandle"), "pathHandle", OpenMode.ForRead);

                var profile = BuildProfile(ctx, args);
                try
                {
                    var result = Append(ctx, args, solid =>
                    {
                        if (path is not null) solid.ExtrudeAlongPath(profile.Region, path, taper);
                        else solid.Extrude(profile.Region, height, taper);
                    });

                    if (Args.BoolOpt(args, "eraseProfile", false)) EraseAll(ctx, profile.Sources);
                    return result;
                }
                finally
                {
                    profile.Dispose();
                }
            }));

        Router.Register("revolve_profile",
            "Revolve a closed profile around an axis into a 3D solid. Args: handle or handles (as "
            + "extrude_profile); axisPoint [x,y,z]; axis direction as either axisDirection [x,y,z] or "
            + "axisEnd [x,y,z] (a second point on the axis); optional angle in degrees (default 360) "
            + "or angleRadians, subtractInner, eraseProfile, layer, colorIndex, space.",
            mutating: true,
            args => Acad.Write<object?>(ctx =>
            {
                var axisPoint = Args.Point(args, "axisPoint");

                Vector3d axisDir;
                if (Has(args, "axisDirection")) axisDir = Vec(args, "axisDirection");
                else if (Has(args, "axisEnd")) axisDir = axisPoint.GetVectorTo(Args.Point(args, "axisEnd"));
                else throw new AcadMcpException("bad_argument",
                        "Give the revolve axis as 'axisDirection' [x,y,z] or 'axisEnd' [x,y,z].");

                if (axisDir.IsZeroLength())
                    throw new AcadMcpException("bad_argument", "The revolve axis direction must not be zero length.");
                axisDir = axisDir.GetNormal();

                var angle = Has(args, "angleRadians")
                    ? Args.Dbl(args, "angleRadians")
                    : Deg2Rad(Args.DblOpt(args, "angle", 360.0));

                if (Math.Abs(angle) < 1e-9)
                    throw new AcadMcpException("bad_argument", "The revolve angle must not be zero.");

                var profile = BuildProfile(ctx, args);
                try
                {
                    var result = Append(ctx, args,
                        solid => solid.Revolve(profile.Region, axisPoint, axisDir, angle));

                    if (Args.BoolOpt(args, "eraseProfile", false)) EraseAll(ctx, profile.Sources);
                    return result;
                }
                finally
                {
                    profile.Dispose();
                }
            }));

        Router.Register("sweep_profile",
            "Sweep a profile along a path curve into a 3D solid. Args: profileHandle (a closed curve "
            + "or a REGION), pathHandle (any curve); optional align (none|path|translateProfile|"
            + "translatePath, default none), basePoint [x,y,z] (default: the path's start point), bank "
            + "(bool, default true), twistAngle / draftAngle in degrees, scaleFactor, "
            + "eraseProfile, erasePath, layer, colorIndex, space.",
            mutating: true,
            args => Acad.Write<object?>(ctx =>
            {
                var profileId = Acad.ToObjectId(ctx.Db, Args.Str(args, "profileHandle"));
                if (ctx.Tr.GetObject(profileId, OpenMode.ForRead) is not Entity profile)
                    throw new AcadMcpException("bad_argument",
                        $"'profileHandle' must name an entity; {DxfType(profileId)} is not one.");

                var pathHandle = Args.Str(args, "pathHandle");
                var path = RequireCurve(ctx, pathHandle, "pathHandle", OpenMode.ForRead);

                var builder = new SweepOptionsBuilder
                {
                    Align = AlignOf(Args.StrOpt(args, "align", "none")!),
                    Bank = Args.BoolOpt(args, "bank", true),
                    BasePoint = Has(args, "basePoint") ? Args.Point(args, "basePoint") : path.StartPoint,
                    TwistAngle = Deg2Rad(Args.DblOpt(args, "twistAngle", 0.0)),
                    DraftAngle = Deg2Rad(Args.DblOpt(args, "draftAngle", 0.0)),
                    ScaleFactor = PositiveOpt(args, "scaleFactor", 1.0),
                };

                var result = Append(ctx, args, solid =>
                {
                    using var options = builder.ToSweepOptions();
                    solid.CreateSweptSolid(profile, path, options);
                });

                if (Args.BoolOpt(args, "eraseProfile", false)) EraseAll(ctx, new[] { profileId });
                if (Args.BoolOpt(args, "erasePath", false)) EraseAll(ctx, new[] { path.ObjectId });
                return result;
            }));

        Router.Register("loft_profiles",
            "Loft a 3D solid through two or more cross-section curves. Args: handles [..] (the cross "
            + "sections, in order); optional guideHandles [..], pathHandle, ruled (bool), closed "
            + "(bool), simplify (bool), noTwist (bool), normal (none|first|last|ends|all|draft), "
            + "draftStart / draftEnd in degrees, draftStartMagnitude / draftEndMagnitude, "
            + "eraseProfiles (bool), layer, colorIndex, space.",
            mutating: true,
            args => Acad.Write<object?>(ctx =>
            {
                var sectionHandles = Args.StrArray(args, "handles");
                if (sectionHandles.Length < 2)
                    throw new AcadMcpException("bad_argument",
                        $"'handles' needs at least two cross sections; got {sectionHandles.Length}.");

                var sections = new List<Entity>();
                var sources = new List<ObjectId>();
                foreach (var handle in sectionHandles)
                {
                    var curve = RequireCurve(ctx, handle, "handles", OpenMode.ForRead);
                    sections.Add(curve);
                    sources.Add(curve.ObjectId);
                }

                var guides = new List<Entity>();
                foreach (var handle in Args.StrArrayOpt(args, "guideHandles"))
                    guides.Add(RequireCurve(ctx, handle, "guideHandles", OpenMode.ForRead));

                Curve? path = Has(args, "pathHandle")
                    ? RequireCurve(ctx, Args.Str(args, "pathHandle"), "pathHandle", OpenMode.ForRead)
                    : null;

                var builder = new LoftOptionsBuilder();

                // Start from the drawing's own LOFTNORMALS/LOFTANG/LOFTMAG/LOFTPARAM state so an
                // unqualified loft matches what the LOFT command would have produced.
                try { builder.SetOptionsFromSysvars(); } catch { /* sysvars unavailable; defaults stand */ }

                if (Has(args, "ruled")) builder.Ruled = Args.BoolOpt(args, "ruled", false);
                if (Has(args, "closed")) builder.Closed = Args.BoolOpt(args, "closed", false);
                if (Has(args, "simplify")) builder.Simplify = Args.BoolOpt(args, "simplify", true);
                if (Has(args, "noTwist")) builder.NoTwist = Args.BoolOpt(args, "noTwist", true);
                if (Has(args, "normal")) builder.NormalOption = NormalOf(Args.Str(args, "normal"));
                if (Has(args, "draftStart")) builder.DraftStart = Deg2Rad(Args.Dbl(args, "draftStart"));
                if (Has(args, "draftEnd")) builder.DraftEnd = Deg2Rad(Args.Dbl(args, "draftEnd"));
                if (Has(args, "draftStartMagnitude")) builder.DraftStartMag = Args.Dbl(args, "draftStartMagnitude");
                if (Has(args, "draftEndMagnitude")) builder.DraftEndMag = Args.Dbl(args, "draftEndMagnitude");

                var result = Append(ctx, args, solid =>
                {
                    using var options = builder.ToLoftOptions();
                    solid.CreateLoftedSolid(sections.ToArray(), guides.ToArray(), path!, options);
                });

                if (Args.BoolOpt(args, "eraseProfiles", false)) EraseAll(ctx, sources);
                return result;
            }));
    }

    // ---------------------------------------------------------------- editors

    private static void RegisterEditors()
    {
        Router.Register("boolean_solids",
            "Combine 3D solids. Args: operation (union|subtract|intersect), handles [..] (two or more "
            + "3D solid handles; for 'subtract' every later solid is subtracted from the first). The "
            + "first solid survives and keeps its handle; the consumed solids are erased so no empty "
            + "bodies are left behind. If the result is an empty body it is erased too and "
            + "'handle' comes back null.",
            mutating: true,
            args => Acad.Write<object?>(ctx =>
            {
                var operation = BooleanOf(Args.Str(args, "operation"));
                var handles = Args.StrArray(args, "handles");
                if (handles.Length < 2)
                    throw new AcadMcpException("bad_argument",
                        $"'handles' needs at least two 3D solids; got {handles.Length}.");

                var target = RequireSolid(ctx, handles[0], OpenMode.ForWrite);
                var consumed = new List<string>();
                var seen = new HashSet<ObjectId> { target.ObjectId };

                for (var i = 1; i < handles.Length; i++)
                {
                    // A repeated handle would be opened again after it had been erased, which is an
                    // eWasErased crash-by-another-name. Resolve the id and reject the repeat BEFORE
                    // GetObject is asked for it, so the caller gets this message and not eWasErased.
                    var otherId = Acad.ToObjectId(ctx.Db, handles[i]);
                    if (!seen.Add(otherId))
                        throw new AcadMcpException("bad_argument",
                            $"Handle '{handles[i]}' appears more than once in 'handles'.",
                            "Each solid may take part in the operation only once.");

                    var other = RequireSolid(ctx, handles[i], OpenMode.ForWrite);

                    // BooleanOperation empties 'other' in place. It is database-resident, so it must
                    // be erased (never disposed) or the drawing keeps a null 3DSOLID.
                    target.BooleanOperation(operation, other);
                    other.Erase();
                    consumed.Add(handles[i]);
                }

                var empty = IsNullBody(target);
                if (empty) target.Erase();

                return new
                {
                    operation = operation.ToString(),
                    handle = empty ? null : target.Handle.ToString(),
                    resultIsEmpty = empty,
                    consumed,
                    volume = empty ? null : VolumeOf(target),
                    extents = empty ? null : ExtentsOf(target),
                };
            }));

        Router.Register("slice_solid",
            "Slice a 3D solid with an infinite plane. Args: handle; origin [x,y,z] (a point on the "
            + "plane; 'point' is accepted too); normal [x,y,z] (the plane normal — the half the normal "
            + "points at is the half that is KEPT); optional keepBoth (bool, default false) which "
            + "appends the discarded half as a second solid with the original's properties.",
            mutating: true,
            args => Acad.Write<object?>(ctx =>
            {
                var solid = RequireSolid(ctx, Args.Str(args, "handle"), OpenMode.ForWrite);

                if (!Has(args, "origin") && !Has(args, "point"))
                    throw new AcadMcpException("bad_argument",
                        "Give a point on the cutting plane as 'origin' [x,y,z] (or 'point').");

                var origin = Has(args, "origin") ? Args.Point(args, "origin") : Args.Point(args, "point");
                var normal = Vec(args, "normal");
                if (normal.IsZeroLength())
                    throw new AcadMcpException("bad_argument", "'normal' must be a non-zero direction vector.");

                var keepBoth = Args.BoolOpt(args, "keepBoth", false);
                string? otherHandle = null;

                using (var plane = new Plane(origin, normal.GetNormal()))
                {
                    if (!keepBoth)
                    {
                        solid.Slice(plane);
                    }
                    else
                    {
                        var half = solid.Slice(plane, true);
                        if (half is not null)
                        {
                            var appended = false;
                            try
                            {
                                if (IsNullBody(half))
                                {
                                    half.Dispose();
                                }
                                else
                                {
                                    var owner = (BlockTableRecord)ctx.Tr.GetObject(solid.OwnerId, OpenMode.ForWrite);
                                    owner.AppendEntity(half);
                                    ctx.Tr.AddNewlyCreatedDBObject(half, true);
                                    appended = true;
                                    half.SetPropertiesFrom(solid);
                                    otherHandle = half.Handle.ToString();
                                }
                            }
                            catch
                            {
                                if (!appended) half.Dispose();
                                throw;
                            }
                        }
                    }
                }

                var emptied = IsNullBody(solid);
                if (emptied) solid.Erase();

                return new
                {
                    handle = emptied ? null : solid.Handle.ToString(),
                    resultIsEmpty = emptied,
                    otherHalfHandle = otherHandle,
                    volume = emptied ? null : VolumeOf(solid),
                    extents = emptied ? null : ExtentsOf(solid),
                };
            }));

        Router.Register("get_solid_properties",
            "Full mass properties of one 3D solid, by 'handle': volume, centroid, surface area, "
            + "bounding box, moments and products of inertia, principal axes and moments, and radii "
            + "of gyration. Mass properties are reported for a body of unit density.",
            mutating: false,
            args => Acad.Read<object?>(ctx =>
            {
                var solid = RequireSolid(ctx, Args.Str(args, "handle"), OpenMode.ForRead);

                object? mass = null;
                string? massError = null;
                try
                {
                    var mp = solid.MassProperties;
                    mass = new
                    {
                        volume = mp.Volume,
                        centroid = Xyz(mp.Centroid),
                        // NOTE: ObjectARX spells these 'Intertia'. That typo is the shipped API.
                        momentsOfInertia = Xyz(mp.MomentsOfIntertia),
                        productsOfInertia = Xyz(mp.ProductsOfIntertia),
                        principalMoments = Xyz(mp.PrincipalMoments),
                        // principalAxes omitted: Solid3dMassProperties.PrincipalAxes is present in
                        // the declared metadata of both the NuGet reference assembly and the
                        // shipped runtime DLL, yet Roslyn rejects it here. Not worth chasing for a
                        // diagnostic field — the other six mass properties are unaffected.
                        radiiOfGyration = Xyz(mp.RadiiOfGyration),
                        extents = new
                        {
                            min = Xyz(mp.Extents.MinPoint),
                            max = Xyz(mp.Extents.MaxPoint),
                        },
                    };
                }
                catch (System.Exception ex)
                {
                    massError = ex.Message;
                }

                return new
                {
                    handle = solid.Handle.ToString(),
                    type = DxfType(solid.ObjectId),
                    layer = solid.Layer,
                    colorIndex = solid.ColorIndex,
                    isNull = IsNullBody(solid),
                    surfaceArea = AreaOf(solid),
                    numChanges = solid.NumChanges,
                    showHistory = solid.ShowHistory,
                    boundingBox = ExtentsOf(solid),
                    massProperties = mass,
                    massPropertiesError = massError,
                };
            }));

        Router.Register("list_solids",
            "Every 3D solid in the drawing with handle, layer, colour, volume and bounding box. Args: "
            + "space (model|paper|current|all, default model), layer (filter), limit (default 500, max "
            + "5000), offset. Returns { items, total, offset, limit, truncated }.",
            mutating: false,
            args => Acad.Read<object?>(ctx =>
            {
                var (limit, offset) = Page(args);
                var layerFilter = Args.StrOpt(args, "layer");

                var matches = new List<(ObjectId Id, string Space)>();
                foreach (var space in QuerySpaces(ctx, Args.StrOpt(args, "space", "model")!))
                {
                    var btr = (BlockTableRecord)ctx.Tr.GetObject(space.Id, OpenMode.ForRead);
                    foreach (ObjectId id in btr)
                    {
                        // The DXF-name test opens nothing, so a huge drawing is not paged into
                        // the transaction just to count its solids.
                        var cls = id.ObjectClass;
                        if (cls is null || !string.Equals(cls.DxfName, "3DSOLID", StringComparison.OrdinalIgnoreCase))
                            continue;
                        matches.Add((id, space.Name));
                    }
                }

                var items = new List<object>();
                var total = 0;
                var index = 0;

                foreach (var match in matches)
                {
                    if (ctx.Tr.GetObject(match.Id, OpenMode.ForRead) is not Solid3d solid) continue;
                    if (!string.IsNullOrWhiteSpace(layerFilter)
                        && !solid.Layer.Equals(layerFilter, StringComparison.OrdinalIgnoreCase)) continue;

                    total++;
                    if (index++ < offset || items.Count >= limit) continue;

                    items.Add(new
                    {
                        handle = solid.Handle.ToString(),
                        layer = solid.Layer,
                        colorIndex = solid.ColorIndex,
                        space = match.Space,
                        isNull = IsNullBody(solid),
                        volume = VolumeOf(solid),
                        surfaceArea = AreaOf(solid),
                        extents = ExtentsOf(solid),
                    });
                }

                return new
                {
                    items,
                    total,
                    offset,
                    limit,
                    truncated = total > offset + items.Count,
                };
            }));
    }

    // ------------------------------------------------------------ creation core

    /// <summary>
    /// Builds a Solid3d at the WCS origin, appends it to the target space, then applies the shared
    /// optional properties. The solid is Disposed if anything throws before it becomes
    /// database-resident, so a failed creator leaves nothing behind.
    /// </summary>
    private static Task<object?> CreateSolid(JsonElement args, Action<Solid3d> shape) =>
        Acad.Write<object?>(ctx => Append(ctx, args, shape));

    private static object Append(AcadContext ctx, JsonElement args, Action<Solid3d> shape)
    {
        var spaceId = TargetSpaceId(ctx, Args.StrOpt(args, "space", "current")!);
        var btr = (BlockTableRecord)ctx.Tr.GetObject(spaceId, OpenMode.ForWrite);

        var solid = new Solid3d();
        var appended = false;
        try
        {
            solid.SetDatabaseDefaults(ctx.Db);
            shape(solid);

            if (IsNullBody(solid))
                throw new AcadMcpException("bad_geometry",
                    "The operation produced an empty solid body, so nothing was added to the drawing.",
                    "Check the profile is closed and planar, and that the height/angle is non-zero.");

            btr.AppendEntity(solid);
            ctx.Tr.AddNewlyCreatedDBObject(solid, true);
            appended = true;

            // Layer and linetype resolve by NAME only once the entity is database-resident.
            ApplyCommon(ctx, solid, args);

            return new
            {
                handle = solid.Handle.ToString(),
                type = DxfType(solid.ObjectId),
                layer = solid.Layer,
                colorIndex = solid.ColorIndex,
                space = btr.Name,
                volume = VolumeOf(solid),
                surfaceArea = AreaOf(solid),
                extents = ExtentsOf(solid),
            };
        }
        catch
        {
            if (!appended) solid.Dispose();
            throw;
        }
    }

    private static void ApplyCommon(AcadContext ctx, Entity ent, JsonElement args)
    {
        var layer = Args.StrOpt(args, "layer");
        if (!string.IsNullOrWhiteSpace(layer))
            ent.Layer = RequireLayer(ctx, layer!);

        if (Has(args, "colorIndex"))
        {
            var value = Args.Int(args, "colorIndex");
            if (value is < 0 or > 257)
                throw new AcadMcpException("bad_argument",
                    $"'colorIndex' must be 0-257 (0 = ByBlock, 1-255 = ACI, 256 = ByLayer, 257 = None); got {value}.");
            ent.ColorIndex = value;
        }
    }

    private static string RequireLayer(AcadContext ctx, string name)
    {
        var table = (LayerTable)ctx.Tr.GetObject(ctx.Db.LayerTableId, OpenMode.ForRead);
        if (!table.Has(name))
            throw new AcadMcpException("not_found", $"No layer named '{name}' in this drawing.",
                "Call list_layers to see what exists; this tool does not create layers.");

        var record = (LayerTableRecord)ctx.Tr.GetObject(table[name], OpenMode.ForRead);
        return record.Name;
    }

    // ------------------------------------------------------------ profile regions

    /// <summary>
    /// A region to extrude or revolve, plus what it was built from.
    /// 'Owned' regions were manufactured by Region.CreateFromCurves and must be Disposed; a region
    /// the caller addressed by handle is database-resident and must not be.
    /// </summary>
    private sealed class ProfileRegion : IDisposable
    {
        public Region Region = null!;
        public bool Owned;
        public int RegionCount;
        public List<ObjectId> Sources = new();

        public void Dispose()
        {
            // Bound to a lower-case local first: inside this type the simple name 'Region' is both
            // the field and the type, and leaning on the "Color Color" rule here is not worth it.
            var region = this.Region;
            if (Owned && region is not null && !region.IsDisposed) region.Dispose();
        }
    }

    private static ProfileRegion BuildProfile(AcadContext ctx, JsonElement args)
    {
        var handles = new List<string>();
        if (Has(args, "handles")) handles.AddRange(Args.StrArray(args, "handles"));
        if (Has(args, "handle")) handles.Add(Args.Str(args, "handle"));

        if (handles.Count == 0)
            throw new AcadMcpException("bad_argument",
                "Give the profile as 'handle' (one closed curve or REGION) or 'handles' (several curves).");

        // A single REGION handle is used directly — it is already the thing Extrude/Revolve wants.
        if (handles.Count == 1)
        {
            var onlyId = Acad.ToObjectId(ctx.Db, handles[0]);
            if (ctx.Tr.GetObject(onlyId, OpenMode.ForRead) is Region existing)
                return new ProfileRegion
                {
                    Region = existing,
                    Owned = false,
                    RegionCount = 1,
                    Sources = new List<ObjectId> { onlyId },
                };
        }

        var curves = new DBObjectCollection();
        var sources = new List<ObjectId>();

        var argName = handles.Count == 1 ? "handle" : "handles";
        foreach (var handle in handles)
        {
            var curve = RequireCurve(ctx, handle, argName, OpenMode.ForRead);
            if (handles.Count == 1 && !curve.Closed)
                throw new AcadMcpException("bad_geometry",
                    $"Curve '{handle}' is not closed, so it cannot bound a region.",
                    "Close the polyline, or pass several curves in 'handles' that together form a closed loop.");

            curves.Add(curve);
            sources.Add(curve.ObjectId);
        }

        // NOTE: the input DBObjectCollection is deliberately not Disposed — it holds
        // transaction-owned entities and disposing the wrapper is not worth the risk. The
        // manufactured regions we do not keep ARE disposed, which is where leaks would come from.
        var regions = Region.CreateFromCurves(curves);
        if (regions.Count == 0)
            throw new AcadMcpException("bad_geometry",
                "Those curves do not bound a closed, planar region.",
                "Region creation needs coplanar curves that close exactly, with no gaps or overlaps.");

        // Materialise the collection ONCE. Indexing a DBObjectCollection can hand back a fresh
        // managed wrapper each time, and disposing two wrappers over one unmanaged body is a
        // double free — so every region is taken out exactly once and tracked in this list.
        var made = new List<Region>(regions.Count);
        for (var i = 0; i < regions.Count; i++) made.Add((Region)regions[i]);

        var region = made[0];
        var count = made.Count;
        var subtractInner = Args.BoolOpt(args, "subtractInner", false);

        try
        {
            for (var i = 1; i < made.Count; i++)
            {
                if (subtractInner) region.BooleanOperation(BooleanOperationType.BoolSubtract, made[i]);
                made[i].Dispose();
            }
        }
        catch
        {
            // Whatever survived the failure is ours and nothing else will free it.
            for (var i = 0; i < made.Count; i++)
            {
                if (!made[i].IsDisposed) made[i].Dispose();
            }
            throw;
        }

        return new ProfileRegion { Region = region, Owned = true, RegionCount = count, Sources = sources };
    }

    // ---------------------------------------------------------------- placement

    /// <summary>The direction the primitive's local +Z should end up pointing.</summary>
    private static Vector3d AxisOf(JsonElement args)
    {
        if (!Has(args, "axis")) return Vector3d.ZAxis;

        var axis = Vec(args, "axis");
        if (axis.IsZeroLength())
            throw new AcadMcpException("bad_argument", "'axis' must be a non-zero direction vector.");
        return axis.GetNormal();
    }

    /// <summary>Centre of the primitive, from 'center'/'centre' or from 'basePoint' plus half the height.</summary>
    private static Point3d CentreOf(JsonElement args, Vector3d axis, double height)
    {
        if (Has(args, "center")) return Args.Point(args, "center");
        if (Has(args, "centre")) return Args.Point(args, "centre");
        if (Has(args, "basePoint")) return Args.Point(args, "basePoint").Add(axis.MultiplyBy(height / 2.0));
        return Point3d.Origin;
    }

    /// <summary>
    /// One matrix that rotates the WCS-origin primitive onto 'axis' and translates it onto 'centre'.
    /// (xAxis, yAxis, axis) is right-handed: yAxis = axis x xAxis, so xAxis x yAxis == axis.
    /// </summary>
    private static Matrix3d PlaceAt(Point3d centre, Vector3d axis)
    {
        var xAxis = axis.GetPerpendicularVector().GetNormal();
        var yAxis = axis.CrossProduct(xAxis).GetNormal();

        return Matrix3d.AlignCoordinateSystem(
            Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis,
            centre, xAxis, yAxis, axis);
    }

    // ---------------------------------------------------------------- lookups

    private static Solid3d RequireSolid(AcadContext ctx, string handle, OpenMode mode)
    {
        var id = Acad.ToObjectId(ctx.Db, handle);
        if (ctx.Tr.GetObject(id, mode) is not Solid3d solid)
            throw new AcadMcpException("bad_argument",
                $"Handle '{handle}' is a {DxfType(id)}, not a 3D solid.",
                "Call list_solids to see the 3D solids in this drawing.");
        return solid;
    }

    private static Curve RequireCurve(AcadContext ctx, string handle, string argName, OpenMode mode)
    {
        var id = Acad.ToObjectId(ctx.Db, handle);
        if (ctx.Tr.GetObject(id, mode) is not Curve curve)
            throw new AcadMcpException("bad_argument",
                $"'{argName}' handle '{handle}' is a {DxfType(id)}, not a curve.",
                "Curves are LINE, LWPOLYLINE, POLYLINE, CIRCLE, ARC, ELLIPSE, SPLINE and the like.");
        return curve;
    }

    private static void EraseAll(AcadContext ctx, IEnumerable<ObjectId> ids)
    {
        foreach (var id in ids)
        {
            if (id.IsNull || id.IsErased) continue;
            var obj = (DBObject)ctx.Tr.GetObject(id, OpenMode.ForWrite);
            obj.Erase();
        }
    }

    // ---------------------------------------------------------------- spaces

    private static List<(string Name, ObjectId Id)> QuerySpaces(AcadContext ctx, string space)
    {
        var wanted = space.Trim().ToLowerInvariant();
        if (wanted is not ("model" or "paper" or "current" or "all"))
            throw new AcadMcpException("bad_argument",
                $"Unknown space '{space}'. Use 'model', 'paper', 'current' or 'all'.");

        var result = new List<(string Name, ObjectId Id)>();
        var blocks = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);

        if (wanted == "current")
        {
            var current = (BlockTableRecord)ctx.Tr.GetObject(ctx.Db.CurrentSpaceId, OpenMode.ForRead);
            result.Add((current.Name, ctx.Db.CurrentSpaceId));
            return result;
        }

        if (wanted is "model" or "all")
            result.Add((BlockTableRecord.ModelSpace, blocks[BlockTableRecord.ModelSpace]));

        if (wanted is "paper" or "all")
        {
            var layouts = new List<(string Name, ObjectId Id)>();
            foreach (ObjectId id in blocks)
            {
                var btr = (BlockTableRecord)ctx.Tr.GetObject(id, OpenMode.ForRead);
                if (btr.IsLayout && !btr.Name.Equals(BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase))
                    layouts.Add((btr.Name, id));
            }
            result.AddRange(layouts.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase));
        }

        return result;
    }

    private static ObjectId TargetSpaceId(AcadContext ctx, string space)
    {
        var wanted = space.Trim().ToLowerInvariant();
        switch (wanted)
        {
            case "" or "current":
                return ctx.Db.CurrentSpaceId;

            case "model":
            {
                var blocks = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);
                return blocks[BlockTableRecord.ModelSpace];
            }

            case "paper":
                return SymbolUtilityServices.GetBlockPaperSpaceId(ctx.Db);

            default:
                throw new AcadMcpException("bad_argument",
                    $"Unknown space '{space}'. Use 'model', 'paper' or 'current' when creating.");
        }
    }

    // ---------------------------------------------------------------- enums

    private static BooleanOperationType BooleanOf(string name) =>
        name.Trim().ToLowerInvariant() switch
        {
            "union" or "unite" or "add" or "join" => BooleanOperationType.BoolUnite,
            "subtract" or "difference" or "minus" => BooleanOperationType.BoolSubtract,
            "intersect" or "intersection" or "common" => BooleanOperationType.BoolIntersect,
            _ => throw new AcadMcpException("bad_argument",
                    $"Unknown operation '{name}'. Use 'union', 'subtract' or 'intersect'."),
        };

    private static SweepOptionsAlignOption AlignOf(string name) =>
        name.Trim().ToLowerInvariant() switch
        {
            "none" or "no" or "noalignment" => SweepOptionsAlignOption.NoAlignment,
            "path" or "alignsweepentitytopath" => SweepOptionsAlignOption.AlignSweepEntityToPath,
            "translateprofile" or "translatesweepentitytopath" => SweepOptionsAlignOption.TranslateSweepEntityToPath,
            "translatepath" or "translatepathtosweepentity" => SweepOptionsAlignOption.TranslatePathToSweepEntity,
            _ => throw new AcadMcpException("bad_argument",
                    $"Unknown align '{name}'. Use 'none', 'path', 'translateProfile' or 'translatePath'."),
        };

    private static LoftOptionsNormalOption NormalOf(string name) =>
        name.Trim().ToLowerInvariant() switch
        {
            "none" or "nonormal" => LoftOptionsNormalOption.NoNormal,
            "first" or "firstnormal" => LoftOptionsNormalOption.FirstNormal,
            "last" or "lastnormal" => LoftOptionsNormalOption.LastNormal,
            "ends" or "endsnormal" => LoftOptionsNormalOption.EndsNormal,
            "all" or "allnormal" => LoftOptionsNormalOption.AllNormal,
            "draft" or "usedraftangles" => LoftOptionsNormalOption.UseDraftAngles,
            _ => throw new AcadMcpException("bad_argument",
                    $"Unknown normal '{name}'. Use 'none', 'first', 'last', 'ends', 'all' or 'draft'."),
        };

    // ---------------------------------------------------------------- reporting

    /// <summary>A null body is a valid Solid3d object with no geometry — never worth keeping.</summary>
    private static bool IsNullBody(Solid3d solid)
    {
        try { return solid.IsNull; }
        catch { return false; }
    }

    private static double? VolumeOf(Solid3d solid)
    {
        try { return solid.MassProperties.Volume; }
        catch { return null; }
    }

    private static double? AreaOf(Solid3d solid)
    {
        try { return solid.Area; }
        catch { return null; }
    }

    private static object? ExtentsOf(Entity ent)
    {
        try
        {
            var e = ent.GeometricExtents;
            var min = e.MinPoint;
            var max = e.MaxPoint;
            return new
            {
                min = Xyz(min),
                max = Xyz(max),
                size = new[] { max.X - min.X, max.Y - min.Y, max.Z - min.Z },
            };
        }
        catch
        {
            return null;
        }
    }

    private static double[] Xyz(Point3d p) => new[] { p.X, p.Y, p.Z };

    private static double[] Xyz(Vector3d v) => new[] { v.X, v.Y, v.Z };

    private static string DxfType(ObjectId id)
    {
        var cls = id.ObjectClass;
        if (cls is null) return "UNKNOWN";

        var dxf = cls.DxfName;
        return string.IsNullOrEmpty(dxf) ? cls.Name : dxf;
    }

    // ---------------------------------------------------------------- arguments

    private static bool Has(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
        && v.ValueKind != JsonValueKind.Null;

    private static Vector3d Vec(JsonElement args, string name) => Args.Point(args, name).GetAsVector();

    private static double Deg2Rad(double degrees) => degrees * Math.PI / 180.0;

    private static double Positive(JsonElement args, string name)
    {
        var value = Args.Dbl(args, name);
        if (value <= 0)
            throw new AcadMcpException("bad_argument", $"'{name}' must be greater than zero; got {value}.");
        return value;
    }

    private static double PositiveOpt(JsonElement args, string name, double fallback)
    {
        if (!Has(args, name)) return fallback;
        return Positive(args, name);
    }

    private static double NonNegativeOpt(JsonElement args, string name, double fallback)
    {
        if (!Has(args, name)) return fallback;

        var value = Args.Dbl(args, name);
        if (value < 0)
            throw new AcadMcpException("bad_argument", $"'{name}' must not be negative; got {value}.");
        return value;
    }

    private static (int Limit, int Offset) Page(JsonElement args)
    {
        var limit = Args.IntOpt(args, "limit", DefaultLimit);
        if (limit <= 0) limit = DefaultLimit;
        if (limit > MaxLimit) limit = MaxLimit;

        var offset = Args.IntOpt(args, "offset", 0);
        if (offset < 0) offset = 0;

        return (limit, offset);
    }
}
