using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace DocumentParsers;

internal static class PptxShapeGeometry
{
    internal static DocumentBoundingBox? Resolve(OpenXmlElement shape, SlidePart slide)
    {
        var local = Transform(shape);
        var placeholder = Placeholder(shape);
        var layout = placeholder is null ? null : slide.SlideLayoutPart?.SlideLayout.CommonSlideData?.ShapeTree?
            .ChildElements.FirstOrDefault(candidate => Placeholder(candidate) is { } inherited &&
                (inherited.Index?.Value ?? 0) == (placeholder.Index?.Value ?? 0));
        var layoutPlaceholder = Placeholder(layout);
        var type = layoutPlaceholder?.Type?.Value ?? placeholder?.Type?.Value ?? P.PlaceholderValues.Object;
        var master = placeholder is null ? null : slide.SlideLayoutPart?.SlideMasterPart?.SlideMaster.CommonSlideData?.ShapeTree?
            .ChildElements.FirstOrDefault(candidate => Placeholder(candidate) is { } inherited &&
                MasterType(inherited.Type?.Value ?? P.PlaceholderValues.Object) == MasterType(type));
        var inheritedTransforms = new[] { local, Transform(layout), Transform(master) };
        var offset = inheritedTransforms.Select(transform => transform?.GetFirstChild<A.Offset>()).FirstOrDefault(value => value is not null);
        var extents = inheritedTransforms.Select(transform => transform?.GetFirstChild<A.Extents>()).FirstOrDefault(value => value is not null);
        // Graphic-frame transforms use PresentationML offset/extents rather than DrawingML.
        if (shape is P.GraphicFrame frame && frame.Transform?.Offset is { } frameOffset)
        {
            return new(frameOffset.X?.Value ?? 0, frameOffset.Y?.Value ?? 0,
                frame.Transform.Extents?.Cx?.Value ?? 0, frame.Transform.Extents?.Cy?.Value ?? 0);
        }
        if (offset?.X is null || offset.Y is null)
        {
            return null;
        }
        return new(offset.X.Value, offset.Y.Value, extents?.Cx?.Value ?? 0, extents?.Cy?.Value ?? 0);
    }

    private static A.Transform2D? Transform(OpenXmlElement? shape) => shape switch
    {
        P.Shape text => text.ShapeProperties?.Transform2D,
        P.Picture picture => picture.ShapeProperties?.Transform2D,
        _ => null
    };

    private static P.PlaceholderShape? Placeholder(OpenXmlElement? shape) => shape switch
    {
        P.Shape text => text.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape,
        P.Picture picture => picture.NonVisualPictureProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape,
        P.GraphicFrame frame => frame.NonVisualGraphicFrameProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape,
        _ => null
    };

    private static P.PlaceholderValues MasterType(P.PlaceholderValues type)
    {
        if (type == P.PlaceholderValues.CenteredTitle)
        {
            return P.PlaceholderValues.Title;
        }
        if (type == P.PlaceholderValues.SubTitle || type == P.PlaceholderValues.Object)
        {
            return P.PlaceholderValues.Body;
        }
        return type;
    }
}
