from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .api_token_response import ApiTokenResponse

@dataclass
class CreatedApiTokenResponse(AdditionalDataHolder, Parsable):
    """
    Returned once, at creation: the only time the secret is visible.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The secret property
    secret: Optional[str] = None
    # The token property
    token: Optional[ApiTokenResponse] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> CreatedApiTokenResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: CreatedApiTokenResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return CreatedApiTokenResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .api_token_response import ApiTokenResponse

        from .api_token_response import ApiTokenResponse

        fields: dict[str, Callable[[Any], None]] = {
            "secret": lambda n : setattr(self, 'secret', n.get_str_value()),
            "token": lambda n : setattr(self, 'token', n.get_object_value(ApiTokenResponse)),
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
        writer.write_str_value("secret", self.secret)
        writer.write_object_value("token", self.token)
        writer.write_additional_data_value(self.additional_data)
    

