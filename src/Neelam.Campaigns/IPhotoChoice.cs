namespace Neelam.Campaigns;

/// <summary>
/// A photo chosen from the client's image library by name, with the text a reader gets when images
/// do not load: the two fields a form binds, whether a template fixes the photo or a campaign
/// chooses it.
/// </summary>
public interface IPhotoChoice
{
    string PhotoName { get; set; }
    string PhotoAltText { get; set; }
}
