
using Damselfly.Core.DbModels.Models.APIModels;
using Damselfly.Core.ScopedServices.ClientServices;
using Damselfly.Core.ScopedServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace Damselfly.Core.ScopedServices;

public class ClientFileService : IFileService
{
    private readonly ILogger<ClientStatusService> _logger;
    private readonly NotificationsService _notifications;
    private readonly RestClient _restClient;

    public ClientFileService( NotificationsService notifications, RestClient restClient, IUserService userService,
        ILogger<ClientStatusService> logger)
    {
        _restClient = restClient;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<bool> DeleteImages(MultiImageRequest req, bool actuallyDeleteImage = false)
    {
        try
        {
            var response = await _restClient.CustomPostAsJsonAsync<MultiImageRequest, bool>( "/api/files/delete", req );

            return response;
        }
        catch( Exception ex )
        {
            _logger.LogError( $"Exception during image delete API call: {ex}" );
        }

        return false;
    }

    public async Task<bool> MoveImages(ImageMoveRequest req)
    {
        try
        {
            var response = await _restClient.CustomPostAsJsonAsync<ImageMoveRequest, bool>( "/api/files/move", req );

            return response;
        }
        catch( Exception ex )
        {
            _logger.LogError( $"Exception during image move API call: {ex}" );
        }

        return false;
    }
}