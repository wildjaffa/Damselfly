using Damselfly.Core.DbModels.Models.API_Models;
using Damselfly.Core.DbModels.Models.APIModels;
using Damselfly.Core.Models;
using Damselfly.Core.ScopedServices.ClientServices;
using Damselfly.Core.ScopedServices.Interfaces;

namespace Damselfly.Core.ScopedServices;

public class ClientPeopleService : IPeopleService
{
    private readonly RestClient httpClient;

    public ClientPeopleService(RestClient client)
    {
        httpClient = client;
    }

    public async Task<Person> GetPerson( Guid personId )
    {
        return await httpClient.CustomGetFromJsonAsync<Person>($"/api/person/{personId}");
    }

    public async Task<List<Person>> GetAllPeople()
    {
        return await httpClient.CustomGetFromJsonAsync<List<Person>>("/api/people");
    }

    public async Task<List<string>> GetPeopleNames(string searchText)
    {
        return await httpClient.CustomGetFromJsonAsync<List<string>>($"/api/people/{searchText}");
    }
    
    public async Task UpdatePersonName(NameChangeRequest req)
    {
        await httpClient.CustomPutAsJsonAsync($"/api/people/name", req);
    }

    public async Task<bool> NeedsAIMigration()
    {
        return await httpClient.CustomGetFromJsonAsync<bool>("/api/people/needsmigration");
    }

    public async Task ExecuteAIMigration(AIMigrationRequest req)
    {
        await httpClient.CustomPostAsJsonAsync("/api/people/runaimigration", req);
    }
}