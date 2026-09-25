from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .application_response import ApplicationResponse

@dataclass
class ApplicationSecretResponse(AdditionalDataHolder, Parsable):
    """
    Returned once, at creation or rotation: the only time the secret is visible.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The application property
    application: Optional[ApplicationResponse] = None
    # The clientSecret property
    client_secret: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ApplicationSecretResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ApplicationSecretResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ApplicationSecretResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .application_response import ApplicationResponse

        from .application_response import ApplicationResponse

        fields: dict[str, Callable[[Any], None]] = {
            "application": lambda n : setattr(self, 'application', n.get_object_value(ApplicationResponse)),
            "clientSecret": lambda n : setattr(self, 'client_secret', n.get_str_value()),
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
        writer.write_object_value("application", self.application)
        writer.write_str_value("clientSecret", self.client_secret)
        writer.write_additional_data_value(self.additional_data)
    

