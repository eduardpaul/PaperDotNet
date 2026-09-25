from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

@dataclass
class CreateApplicationRequest(AdditionalDataHolder, Parsable):
    """
    A new OAuth client. `clientType`: `confidential` (has a secret) or `public`(native/SPA, PKCE only). `grantTypes`: `authorization_code`, `refresh_token`,`client_credentials` (confidential only; the client acts as its own service account).`scopes`: permission scopes, or `api` for full access as the user.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The clientType property
    client_type: Optional[str] = None
    # The displayName property
    display_name: Optional[str] = None
    # The grantTypes property
    grant_types: Optional[list[str]] = None
    # The postLogoutRedirectUris property
    post_logout_redirect_uris: Optional[list[str]] = None
    # The redirectUris property
    redirect_uris: Optional[list[str]] = None
    # The scopes property
    scopes: Optional[list[str]] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> CreateApplicationRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: CreateApplicationRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return CreateApplicationRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "clientType": lambda n : setattr(self, 'client_type', n.get_str_value()),
            "displayName": lambda n : setattr(self, 'display_name', n.get_str_value()),
            "grantTypes": lambda n : setattr(self, 'grant_types', n.get_collection_of_primitive_values(str)),
            "postLogoutRedirectUris": lambda n : setattr(self, 'post_logout_redirect_uris', n.get_collection_of_primitive_values(str)),
            "redirectUris": lambda n : setattr(self, 'redirect_uris', n.get_collection_of_primitive_values(str)),
            "scopes": lambda n : setattr(self, 'scopes', n.get_collection_of_primitive_values(str)),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_str_value("clientType", self.client_type)
        writer.write_str_value("displayName", self.display_name)
        writer.write_collection_of_primitive_values("grantTypes", self.grant_types)
        writer.write_collection_of_primitive_values("postLogoutRedirectUris", self.post_logout_redirect_uris)
        writer.write_collection_of_primitive_values("redirectUris", self.redirect_uris)
        writer.write_collection_of_primitive_values("scopes", self.scopes)
        writer.write_additional_data_value(self.additional_data)
    

