using MongoDB.Driver;
using Pole.Features.Users.Application.Registration;
using Pole.Features.Users.Domain;

namespace Pole.Features.Users.Application.Mongo;

internal static class UserFilters
{
    public static FilterDefinition<PoleUser> ByEmail(string email) =>
        Builders<PoleUser>.Filter.Eq(user => user.Email, email);
}
