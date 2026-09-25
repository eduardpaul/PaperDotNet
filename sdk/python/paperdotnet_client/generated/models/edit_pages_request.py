from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .page_spec import PageSpec

@dataclass
class EditPagesRequest(AdditionalDataHolder, Parsable):
    """
    The pages of the new version in order: pages left out are deleted (DOC-05).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The pages property
    pages: Optional[list[PageSpec]] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> EditPagesRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: EditPagesRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return EditPagesRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .page_spec import PageSpec

        from .page_spec import PageSpec

        fields: dict[str, Callable[[Any], None]] = {
            "pages": lambda n : setattr(self, 'pages', n.get_collection_of_object_values(PageSpec)),
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
        writer.write_collection_of_object_values("pages", self.pages)
        writer.write_additional_data_value(self.additional_data)
    

