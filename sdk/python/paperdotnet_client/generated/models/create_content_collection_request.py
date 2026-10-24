from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

@dataclass
class CreateContentCollectionRequest(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The library property
    library: Optional[bool] = False
    # The publishingEnabled property
    publishing_enabled: Optional[bool] = False
    # The name property
    name: Optional[str] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> CreateContentCollectionRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: CreateContentCollectionRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return CreateContentCollectionRequest()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "library": lambda n : setattr(self, 'library', n.get_bool_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "publishingEnabled": lambda n : setattr(self, 'publishing_enabled', n.get_bool_value()),
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
        writer.write_bool_value("library", self.library)
        writer.write_str_value("name", self.name)
        writer.write_bool_value("publishingEnabled", self.publishing_enabled)
        writer.write_additional_data_value(self.additional_data)


