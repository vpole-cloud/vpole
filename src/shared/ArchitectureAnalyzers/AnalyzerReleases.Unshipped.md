## Unreleased

### New Rules

| Rule ID | Category     | Severity | Notes                                                                                       |
| ------- | ------------ | -------- | ------------------------------------------------------------------------------------------- |
| POLE001 | Architecture | Error    | Mongo query APIs must remain inside persistence capabilities.                               |
| POLE002 | Architecture | Error    | Endpoint handlers own colocated outcome types.                                              |
| POLE003 | Architecture | Error    | Factory-owned domain entities are constructed through their factory.                        |
| POLE004 | Architecture | Error    | Factory-owned domain entities do not expose mutable collection properties.                  |
| POLE005 | Architecture | Error    | Domain State, Status and Lifecycle properties use dedicated types.                          |
| POLE006 | Architecture | Error    | Each endpoint file contains only one mediator request-handler pair.                         |
| POLE007 | Architecture | Error    | Multi-parameter records, unions and delegates use one parameter per line.                   |
| POLE008 | Architecture | Error    | Declare Mongo filters in named helpers                                                      |
| POLE009 | Architecture | Error    | Stored record data uses one constructor/property style; exact legacy members are ratcheted. |

| POLE010 | Architecture | Error | Feature-owned Mongo root models belong to the owning Domain project. |
| POLE011 | Architecture | Error | Domain constructor parameters have no optional defaults. |

| POLE012 | Architecture | Error | Closed hierarchy switches name variants instead of a catch-all. |

| POLE013 | Architecture | Error | Runtime Mongo field filters use typed expressions instead of string literals. |
